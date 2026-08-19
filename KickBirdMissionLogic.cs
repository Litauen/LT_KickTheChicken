using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace LT_KickTheChicken
{
    /// <summary>
    /// Kick chicken/goose agents into a short live ballistic arc, then panic-run;
    /// nearby birds flee and nearby humans show a scare reaction.
    /// </summary>
    internal class KickBirdMissionLogic : MissionLogic
    {
        private const float KickHorizontalSpeedMin = 2.5f;
        private const float KickHorizontalSpeedMax = 9.5f;
        private const float KickVerticalSpeedMin = 2f;
        private const float KickVerticalSpeedMax = 9.5f;
        private const float LaunchLiftMin = 0.1f;
        private const float LaunchLiftMax = 0.55f;
        private const float KickAthleticsSkillMax = 250f;
        private const float KickPowerRandomMin = 0.7f;
        private const float KickPowerRandomMax = 1.3f;
        // Geese are heavy: same arc height/airtime as a chicken, half the range.
        private const float GooseKickDistanceFactor = 0.5f;
        private const float ProximityKickRange = 1.75f;
        private const float ProximityKickFacingDot = 0.25f;
        // Leg is fully extended roughly this long after the kick action starts;
        // launching at the rising edge looked like the bird flew before contact.
        private const float KickLaunchDelaySeconds = 0.25f;
        private const float KickLaunchMaxRange = 2.5f;
        private const float NearbyBirdPanicRadius = 8f;
        private const float NearbyHumanAweRadius = 11f;
        private const float FleeDistanceMin = 8f;
        private const float FleeDistanceMax = 14f;
        private const float FleeDurationSeconds = 5f;
        private const float FleeSpeedMultiplier = 1.35f;
        private const float BirdPanicCooldownSeconds = 2.5f;
        private const float HumanAweDurationSeconds = 10f;
        private const float HumanLaughChance = 0.35f;
        private const float MaxFlightSeconds = 3.5f;
        private const float MinAirborneSeconds = 0.2f;
        private const float GroundClearance = 0.12f;
        private const float WallRayLift = 0.35f;
        private const float WallRayThickness = 0.05f;
        private const float WallHitAboveGround = 0.45f;
        private const float LandingDownSpeed = -0.35f;
        private const float TumbleSpinRateMin = 4f;
        private const float TumbleSpinRateMax = 8f;
        private const float AirSoundIntervalMin = 0.28f;
        private const float AirSoundIntervalMax = 0.42f;
        // When the visual lands off-navmesh, search nearby / on a same-distance
        // kick cone so the bird reappears near the touchdown, not at the kicker.
        private const float LandNavSearchRadiusMax = 8f;
        private const float LandNavSearchStep = 0.75f;
        private const int LandNavSearchAngles = 12;
        private const float LandConeHalfAngleDegrees = 55f;
        private const int LandConeAngleSteps = 7;
        // Crash tracing off — CTD fixed (agent no longer teleports during flight).
        private const float CrashTraceSeconds = 0f;
        // Routine BirdKick breadcrumbs stay off; LogError still always writes.
        private const bool EnableDebugLogs = false;

        // chicken/death is the audible distressed vocal (fly is near-silent wing foley).
        private const string ChickenAirSoundEvent = "event:/mission/movement/foley/animals/chicken/death";
        private const string ChickenLandSoundEvent = "event:/mission/movement/foley/animals/chicken/death";
        private const string GooseAirSoundEvent = "event:/mission/movement/foley/animals/goose/death";
        private const string GooseLandSoundEvent = "event:/mission/movement/foley/animals/goose/death";
        private const string LandImpactSoundEvent = "event:/mission/combat/impact/corpse";
        private const string KickFoleyEvent = "event:/mission/combat/kick";

        // Conversation gestures confirmed in CharacterDebugSpawner / ActionIndexCache.
        private static readonly ActionIndexCache ActThreatConversation = ActionIndexCache.Create("act_threat_conversation");
        private static readonly ActionIndexCache ActNegativeConversation = ActionIndexCache.Create("act_negative_conversation");
        private static readonly ActionIndexCache ActLaughConversation = ActionIndexCache.Create("act_laugh_conversation");
        private static readonly ActionIndexCache ActWonderingConversation = ActionIndexCache.Create("act_wondering_conversation");
        private static readonly ActionIndexCache ActUnknownConversation = ActionIndexCache.Create("act_unknown_conversation");

        private readonly Dictionary<Agent, FlyingBirdState> _flyingBirds = new Dictionary<Agent, FlyingBirdState>();
        private readonly Dictionary<Agent, FleeingBirdState> _fleeingBirds = new Dictionary<Agent, FleeingBirdState>();
        private readonly Dictionary<Agent, float> _birdPanicCooldownUntil = new Dictionary<Agent, float>();
        private readonly Dictionary<Agent, float> _awedHumansUntil = new Dictionary<Agent, float>();
        private readonly List<(Vec3 Pos, Agent Bird)> _pendingCrowdReactions = new List<(Vec3, Agent)>();
        private readonly List<(Agent Bird, Agent Kicker, float LaunchAt)> _pendingKickLaunches = new List<(Agent, Agent, float)>();

        private bool _mainWasKicking;

        private sealed class FlyingBirdState
        {
            public Vec3 SimPos;
            public Vec3 Velocity;
            public AgentFlag SavedFlags;
            public GameEntity? VisualEntity;
            public Mat3 VisualBaseRotation;
            public string MeshName = string.Empty;
            public string AirSoundEvent = string.Empty;
            public float NextAirSoundAt;
            public float TumbleAngle;
            public float TumbleRate;
            public float Elapsed;
        }

        private sealed class FleeingBirdState
        {
            public AgentFlag SavedFlags;
            public float EndsAt;
            public bool HadWander;
            public float NextTraceAt;
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            if (Mission == null || Mission.Scene == null)
            {
                return;
            }

            TickProximityKickAssist();
            TickPendingKickLaunches();
            TickPendingCrowdReactions();
            TickFlight(dt);
            TickFleeRestore();
            TickHumanAweRestore();

            // If the last trace line before a CTD is z-missionTickEnd, the crash is
            // in the engine's own frame update, not in any call this logic makes.
            foreach (KeyValuePair<Agent, FlyingBirdState> pair in _flyingBirds)
            {
                if (pair.Value.Elapsed < CrashTraceSeconds)
                {
                    DebugLog($"[BirdKick] TRACE z-missionTickEnd t={pair.Value.Elapsed:0.###}");
                    break;
                }
            }
        }

        public override void OnMeleeHit(Agent attacker, Agent victim, bool isCanceled, AttackCollisionData collisionData)
        {
            base.OnMeleeHit(attacker, victim, isCanceled, collisionData);
            if (!IsMainKickAttacker(attacker) || victim == null || !IsKickableBird(victim))
            {
                return;
            }

            if (!collisionData.IsAlternativeAttack || !attacker.WieldedWeapon.IsEmpty)
            {
                return;
            }

            DebugLog($"[BirdKick] OnMeleeHit bird={GetBirdId(victim)} canceled={isCanceled} flying={_flyingBirds.ContainsKey(victim)}");
            TryLaunchBird(victim, attacker);
        }

        public override void OnAgentHit(Agent affectedAgent, Agent affectorAgent, in MissionWeapon affectorWeapon, in Blow blow, in AttackCollisionData attackCollisionData)
        {
            base.OnAgentHit(affectedAgent, affectorAgent, in affectorWeapon, in blow, in attackCollisionData);
            if (affectedAgent == null || !IsKickableBird(affectedAgent))
            {
                return;
            }

            if (blow.AttackType != AgentAttackType.Kick || !IsMainKickAttacker(affectorAgent))
            {
                return;
            }

            float healthBeforeRestore = affectedAgent.Health;
            float restored = MathF.Clamp(affectedAgent.Health + blow.InflictedDamage, 1f, affectedAgent.HealthLimit);
            affectedAgent.Health = restored;
            DebugLog($"[BirdKick] OnAgentHit death-guard bird={GetBirdId(affectedAgent)} dmg={blow.InflictedDamage} hpAfterBlow={healthBeforeRestore:0.##} restored={restored:0.##}/{affectedAgent.HealthLimit:0.##}");

            TryLaunchBird(affectedAgent, affectorAgent);
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            base.OnAgentRemoved(affectedAgent, affectorAgent, agentState, blow);
            if (affectedAgent == null)
            {
                return;
            }

            if (_flyingBirds.TryGetValue(affectedAgent, out FlyingBirdState flying))
            {
                ReleaseFlightAttachment(affectedAgent, flying, removeEntity: true);
                _flyingBirds.Remove(affectedAgent);
            }

            _fleeingBirds.Remove(affectedAgent);
            _birdPanicCooldownUntil.Remove(affectedAgent);
            _awedHumansUntil.Remove(affectedAgent);
            _pendingKickLaunches.RemoveAll(p => p.Bird == affectedAgent || p.Kicker == affectedAgent);
        }

        public override void OnClearScene()
        {
            base.OnClearScene();
            foreach (KeyValuePair<Agent, FlyingBirdState> pair in _flyingBirds)
            {
                ReleaseFlightAttachment(pair.Key, pair.Value, removeEntity: true);
            }

            _flyingBirds.Clear();
            _fleeingBirds.Clear();
            _birdPanicCooldownUntil.Clear();
            _awedHumansUntil.Clear();
            _pendingCrowdReactions.Clear();
            _pendingKickLaunches.Clear();
            _mainWasKicking = false;
        }

        private void TickProximityKickAssist()
        {
            Agent main = Agent.Main;
            if (main == null || !main.IsActive())
            {
                _mainWasKicking = false;
                return;
            }

            bool isKicking = IsAgentPerformingKick(main);
            bool risingEdge = isKicking && !_mainWasKicking;
            _mainWasKicking = isKicking;
            if (!risingEdge)
            {
                return;
            }

            DebugLog($"[BirdKick] Kick rising-edge ch0={main.GetCurrentActionType(0)} ch1={main.GetCurrentActionType(1)}");

            Agent? best = null;
            float bestDistSq = ProximityKickRange * ProximityKickRange;
            Vec2 look = main.LookDirection.AsVec2;
            if (look.LengthSquared < 0.0001f)
            {
                look = main.GetMovementDirection();
            }

            look = look.Normalized();
            AgentProximityMap.ProximityMapSearchStruct search = AgentProximityMap.BeginSearch(Mission, main.Position.AsVec2, ProximityKickRange);
            while (search.LastFoundAgent != null)
            {
                Agent agent = search.LastFoundAgent;
                AgentProximityMap.FindNext(Mission, ref search);
                if (!IsKickableBird(agent) || _flyingBirds.ContainsKey(agent) || !agent.IsActive())
                {
                    continue;
                }

                Vec2 toBird = (agent.Position - main.Position).AsVec2;
                float distSq = toBird.LengthSquared;
                if (distSq > bestDistSq || distSq < 0.0001f)
                {
                    continue;
                }

                float facing = Vec2.DotProduct(look, toBird.Normalized());
                if (facing < ProximityKickFacingDot)
                {
                    continue;
                }

                best = agent;
                bestDistSq = distSq;
            }

            if (best != null)
            {
                // Delay so the launch matches the moment the leg is extended,
                // instead of the first frame of the kick animation.
                float launchAt = Mission.CurrentTime + KickLaunchDelaySeconds;
                _pendingKickLaunches.Add((best, main, launchAt));
                DebugLog($"[BirdKick] Proximity assist queued bird={GetBirdId(best)} dist={MathF.Sqrt(bestDistSq):0.##} launchAt={launchAt:0.##}");
            }
        }

        private void TickPendingKickLaunches()
        {
            if (_pendingKickLaunches.Count == 0)
            {
                return;
            }

            float now = Mission.CurrentTime;
            for (int i = _pendingKickLaunches.Count - 1; i >= 0; i--)
            {
                (Agent bird, Agent kicker, float launchAt) = _pendingKickLaunches[i];
                if (now < launchAt)
                {
                    continue;
                }

                _pendingKickLaunches.RemoveAt(i);
                if (bird == null || !bird.IsActive() || _flyingBirds.ContainsKey(bird))
                {
                    continue;
                }

                // Bird may have wandered off during the wind-up.
                if (kicker != null && kicker.IsActive()
                    && (bird.Position - kicker.Position).AsVec2.LengthSquared > KickLaunchMaxRange * KickLaunchMaxRange)
                {
                    DebugLog($"[BirdKick] Proximity assist expired bird={GetBirdId(bird)} (out of range at launch)");
                    continue;
                }

                DebugLog($"[BirdKick] Proximity assist launch bird={GetBirdId(bird)}");
                TryLaunchBird(bird, kicker);
            }
        }

        private void TickFlight(float dt)
        {
            if (_flyingBirds.Count == 0)
            {
                return;
            }

            List<Agent>? toLand = null;
            Scene scene = Mission.Scene;
            foreach (KeyValuePair<Agent, FlyingBirdState> pair in _flyingBirds)
            {
                Agent bird = pair.Key;
                FlyingBirdState state = pair.Value;
                if (!bird.IsActive())
                {
                    toLand ??= new List<Agent>();
                    toLand.Add(bird);
                    continue;
                }

                state.Elapsed += dt;
                state.Velocity += MBGlobals.GravitationalAcceleration * dt;
                // Confirmed dead ends for lifting a free agent: Teleport Z,
                // SetTargetZ, AgentVisuals.SetFrame, SetForceAttachedEntity.
                // Visible flight is a free GameEntity mesh; the real agent stays
                // grounded/invisible and is restored on landing.
                bool trace = state.Elapsed < CrashTraceSeconds;
                Vec3 current = state.SimPos;
                Vec3 next = current + state.Velocity * dt;

                if (trace)
                {
                    DebugLog($"[BirdKick] TRACE a-heightCur t={state.Elapsed:0.###} at=({current.x:0.##},{current.y:0.##},{current.z:0.##})");
                }

                float probeGroundZ = current.z;
                if (!scene.GetHeightAtPoint(current.AsVec2, BodyFlags.CommonCollisionExcludeFlagsForAgent, ref probeGroundZ))
                {
                    probeGroundZ = scene.GetGroundHeightAtPosition(current);
                }

                Vec3 rayStart = current + Vec3.Up * WallRayLift;
                Vec3 rayEnd = next + Vec3.Up * WallRayLift;
                float travelLen = (rayEnd - rayStart).Length;
                if (trace)
                {
                    DebugLog($"[BirdKick] TRACE b-ray t={state.Elapsed:0.###} len={travelLen:0.###}");
                }

                if (travelLen > 0.001f
                    && scene.RayCastForClosestEntityOrTerrain(rayStart, rayEnd, out float collisionDistance, out Vec3 closestPoint, WallRayThickness, BodyFlags.CommonCollisionExcludeFlagsForAgent)
                    && collisionDistance > 0.15f
                    && collisionDistance < travelLen
                    && closestPoint.z > probeGroundZ + WallHitAboveGround)
                {
                    Vec3 hitDelta = closestPoint - current;
                    next = new Vec3(closestPoint.x, closestPoint.y, MathF.Max(closestPoint.z, next.z));
                    Vec2 horizontal = state.Velocity.AsVec2;
                    if (horizontal.LengthSquared > 0.01f)
                    {
                        state.Velocity = new Vec3(-horizontal.x * 0.35f, -horizontal.y * 0.35f, state.Velocity.z);
                    }

                    DebugLog($"[BirdKick] Flight wall-hit bird={GetBirdId(bird)} dist={collisionDistance:0.##} hitZ={closestPoint.z:0.##} groundZ={probeGroundZ:0.##} dxy={hitDelta.AsVec2.Length:0.##}");
                }

                if (trace)
                {
                    DebugLog($"[BirdKick] TRACE c-heightNext t={state.Elapsed:0.###} next=({next.x:0.##},{next.y:0.##},{next.z:0.##})");
                }

                float groundZ = next.z;
                float height = next.z;
                if (scene.GetHeightAtPoint(next.AsVec2, BodyFlags.CommonCollisionExcludeFlagsForAgent, ref height))
                {
                    groundZ = height;
                }
                else
                {
                    groundZ = scene.GetGroundHeightAtPosition(next);
                }

                bool pastMinAirborne = state.Elapsed >= MinAirborneSeconds;
                bool shouldLand = pastMinAirborne && next.z <= groundZ + GroundClearance && state.Velocity.z <= LandingDownSpeed;
                if (!shouldLand && state.Elapsed >= MaxFlightSeconds)
                {
                    shouldLand = true;
                    DebugLog($"[BirdKick] Flight timeout bird={GetBirdId(bird)} t={state.Elapsed:0.##}");
                }

                if (shouldLand)
                {
                    next.z = groundZ + GroundClearance;
                    state.SimPos = next;
                    ApplyFlightVisual(state, next, trace);
                    toLand ??= new List<Agent>();
                    toLand.Add(bird);
                    continue;
                }

                if (next.z < groundZ + GroundClearance)
                {
                    next.z = groundZ + GroundClearance;
                }

                state.SimPos = next;
                state.TumbleAngle += state.TumbleRate * dt;
                ApplyFlightVisual(state, next, trace);
                if (trace)
                {
                    DebugLog($"[BirdKick] TRACE e-sound t={state.Elapsed:0.###}");
                }

                TickAirSound(state);
                if (trace)
                {
                    DebugLog($"[BirdKick] TRACE f-tickDone t={state.Elapsed:0.###}");
                }
                if ((int)(state.Elapsed * 10f) != (int)((state.Elapsed - dt) * 10f))
                {
                    float visZ = state.VisualEntity != null ? state.VisualEntity.GlobalPosition.z : float.NaN;
                    bool agentVisible = bird.AgentVisuals != null && bird.AgentVisuals.IsValid() && bird.AgentVisuals.GetVisible();
                    DebugLog($"[BirdKick] Flight tick bird={GetBirdId(bird)} t={state.Elapsed:0.##} simZ={next.z:0.##} agentZ={bird.Position.z:0.##} visZ={visZ:0.##} agentVis={agentVisible} mesh={state.MeshName} ground={groundZ:0.##} vz={state.Velocity.z:0.##}");
                }
            }

            if (toLand == null)
            {
                return;
            }

            for (int i = 0; i < toLand.Count; i++)
            {
                Agent bird = toLand[i];
                if (_flyingBirds.TryGetValue(bird, out FlyingBirdState state))
                {
                    LandBird(bird, state);
                }
            }
        }

        private void TickFleeRestore()
        {
            if (_fleeingBirds.Count == 0)
            {
                return;
            }

            float now = Mission.CurrentTime;
            List<Agent>? done = null;
            foreach (KeyValuePair<Agent, FleeingBirdState> pair in _fleeingBirds)
            {
                Agent bird = pair.Key;
                FleeingBirdState state = pair.Value;
                if (!bird.IsActive() || now >= state.EndsAt)
                {
                    done ??= new List<Agent>();
                    done.Add(bird);
                    continue;
                }

                // Crash forensics (only when debug logs are on).
                if (EnableDebugLogs && now >= state.NextTraceAt)
                {
                    state.NextTraceAt = now + 0.3f;
                    Vec3 pos = bird.Position;
                    bool onMesh = IsWalkableNavMesh(Mission.Scene, pos);
                    DebugLog($"[BirdKick] TRACE flee bird={GetBirdId(bird)} pos=({pos.x:0.##},{pos.y:0.##},{pos.z:0.##}) onMesh={onMesh}");
                }
            }

            if (done == null)
            {
                return;
            }

            for (int i = 0; i < done.Count; i++)
            {
                RestoreFleeingBird(done[i]);
            }
        }

        private void TryLaunchBird(Agent bird, Agent? kicker)
        {
            if (bird == null || !bird.IsActive() || !IsKickableBird(bird) || _flyingBirds.ContainsKey(bird))
            {
                return;
            }

            AgentFlag savedFlags = bird.GetAgentFlags();
            bird.SetAgentFlags(savedFlags & ~AgentFlag.CanWander);
            bird.ClearTargetFrame();
            bird.DisableScriptedMovement();
            bird.SetMortalityState(Agent.MortalityState.Invulnerable);

            GetKickLaunchPower(kicker, out float horizontalSpeed, out float verticalSpeed, out float launchLift, out int athletics, out float powerFactor);
            if (IsGoose(bird))
            {
                horizontalSpeed *= GooseKickDistanceFactor;
            }

            Vec3 liftPos = bird.Position;
            liftPos.z += launchLift;

            Vec3 forward = Vec3.Zero;
            if (kicker != null)
            {
                forward = kicker.LookDirection;
                if (forward.AsVec2.LengthSquared < 0.0001f)
                {
                    Vec2 move = kicker.GetMovementDirection();
                    forward = new Vec3(move.x, move.y, 0f);
                }
            }

            if (forward.AsVec2.LengthSquared < 0.0001f)
            {
                Vec2 away = (bird.Position - (kicker?.Position ?? bird.Position)).AsVec2;
                if (away.LengthSquared < 0.0001f)
                {
                    away = Vec2.Forward;
                }

                away = away.Normalized();
                forward = new Vec3(away.x, away.y, 0f);
            }

            forward = new Vec3(forward.x, forward.y, 0f);
            if (forward.LengthSquared < 0.0001f)
            {
                forward = Vec3.Forward;
            }
            else
            {
                forward = forward.NormalizedCopy();
            }

            Mat3 visualRotation = Mat3.Identity;
            visualRotation.RotateAboutUp(MathF.Atan2(forward.x, forward.y));

            string meshName = GetBirdMeshName(bird);
            GameEntity? visual = TryCreateFlightVisual(Mission.Scene, meshName, visualRotation, liftPos);

            // Hide the grounded agent; the free mesh is what the player sees.
            bird.SetRenderCheckEnabled(value: false);
            if (bird.AgentVisuals != null && bird.AgentVisuals.IsValid())
            {
                bird.AgentVisuals.SetVisible(false);
            }

            float tumbleSign = MBRandom.RandomFloat > 0.5f ? 1f : -1f;
            string airSound = GetBirdAirSoundEvent(bird);
            FlyingBirdState state = new FlyingBirdState
            {
                SimPos = liftPos,
                Velocity = forward * horizontalSpeed + Vec3.Up * verticalSpeed,
                SavedFlags = savedFlags,
                VisualEntity = visual,
                VisualBaseRotation = visualRotation,
                MeshName = meshName,
                AirSoundEvent = airSound,
                NextAirSoundAt = 0f,
                TumbleAngle = 0f,
                TumbleRate = tumbleSign * MBRandom.RandomFloatRanged(TumbleSpinRateMin, TumbleSpinRateMax),
                Elapsed = 0f
            };
            _flyingBirds[bird] = state;

            // Immediate terrified cry as it leaves the ground, then repeats while airborne.
            TickAirSound(state);
            Vec3 kickPos = liftPos;
            SoundManager.StartOneShotEvent(KickFoleyEvent, in kickPos);

            DebugLog($"[BirdKick] Launch bird={GetBirdId(bird)} mode=meshProxy visual={(visual != null)} mesh={meshName} athletics={athletics} power={powerFactor:0.##} airSound={airSound} class={bird.GetSoundAndCollisionInfoClassName()} simZ={liftPos.z:0.##} vel=({state.Velocity.x:0.##},{state.Velocity.y:0.##},{state.Velocity.z:0.##}) tumbleRate={state.TumbleRate:0.##}");

            // Defer crowd AI so a flee/scripted-move CTD cannot land in the same
            // frame as mesh spawn / SetVisible (last crash stopped after Crowd log,
            // before the first Flight tick).
            _pendingCrowdReactions.Add((liftPos, bird));
            DebugLog($"[BirdKick] Launch queued-crowd bird={GetBirdId(bird)}");
        }

        private void TickPendingCrowdReactions()
        {
            if (_pendingCrowdReactions.Count == 0)
            {
                return;
            }

            List<(Vec3 Pos, Agent Bird)> pending = new List<(Vec3, Agent)>(_pendingCrowdReactions);
            _pendingCrowdReactions.Clear();
            for (int i = 0; i < pending.Count; i++)
            {
                (Vec3 pos, Agent bird) = pending[i];
                DebugLog($"[BirdKick] Crowd deferred-begin bird={GetBirdId(bird)}");
                try
                {
                    TriggerCrowdReactions(pos, bird);
                }
                catch (System.Exception ex)
                {
                    KtcLogger.LogError($"[BirdKick] Crowd deferred exception bird={GetBirdId(bird)}");
                    KtcLogger.LogError(ex);
                }

                DebugLog($"[BirdKick] Crowd deferred-end bird={GetBirdId(bird)}");
            }
        }

        private static void TickAirSound(FlyingBirdState state)
        {
            if (string.IsNullOrEmpty(state.AirSoundEvent) || state.Elapsed < state.NextAirSoundAt)
            {
                return;
            }

            Vec3 soundPos = state.SimPos;
            bool played = SoundManager.StartOneShotEvent(state.AirSoundEvent, in soundPos);
            state.NextAirSoundAt = state.Elapsed + MBRandom.RandomFloatRanged(AirSoundIntervalMin, AirSoundIntervalMax);
            if (!played)
            {
                DebugLog($"[BirdKick] Air sound FAILED event={state.AirSoundEvent} at=({soundPos.x:0.##},{soundPos.y:0.##},{soundPos.z:0.##})");
            }
        }

        private static void GetKickLaunchPower(Agent? kicker, out float horizontalSpeed, out float verticalSpeed, out float launchLift, out int athletics, out float powerFactor)
        {
            athletics = 0;
            if (kicker?.Character != null)
            {
                athletics = kicker.Character.GetSkillValue(DefaultSkills.Athletics);
            }

            float skillT = MathF.Clamp(athletics / KickAthleticsSkillMax, 0f, 1f);
            powerFactor = MBRandom.RandomFloatRanged(KickPowerRandomMin, KickPowerRandomMax);
            horizontalSpeed = MathF.Lerp(KickHorizontalSpeedMin, KickHorizontalSpeedMax, skillT) * powerFactor;
            verticalSpeed = MathF.Lerp(KickVerticalSpeedMin, KickVerticalSpeedMax, skillT) * powerFactor;
            launchLift = MathF.Lerp(LaunchLiftMin, LaunchLiftMax, skillT) * powerFactor;
        }

        private void LandBird(Agent bird, FlyingBirdState state)
        {
            _flyingBirds.Remove(bird);
            if (bird == null || !bird.IsActive())
            {
                ReleaseFlightAttachment(bird, state, removeEntity: true);
                return;
            }

            Vec3 desiredLand = state.SimPos;
            float groundZ = desiredLand.z;
            if (!Mission.Scene.GetHeightAtPoint(desiredLand.AsVec2, BodyFlags.CommonCollisionExcludeFlagsForAgent, ref groundZ))
            {
                groundZ = Mission.Scene.GetGroundHeightAtPosition(desiredLand);
            }

            desiredLand.z = groundZ + GroundClearance;
            // Agent waited at the kick spot during flight — that is the origin for
            // same-distance cone search when the touchdown is a navmesh hole.
            Vec3 kickOrigin = bird.Position;
            ReleaseFlightAttachment(bird, state, removeEntity: true);

            Vec3 landPos;
            string landMode;
            if (TryResolveLandingOnNavMesh(desiredLand, kickOrigin, out landPos, out landMode))
            {
                bird.TeleportToPosition(landPos);
            }
            else
            {
                landPos = bird.Position;
                landMode = "fallbackKickSpot";
                DebugLog($"[BirdKick] Land no safe navmesh near touchdown; bird stays at kick spot");
            }

            bird.ClearTargetFrame();
            bird.SetMortalityState(Agent.MortalityState.Mortal);

            string landBirdSound = GetBirdLandSoundEvent(bird);
            SoundManager.StartOneShotEvent(LandImpactSoundEvent, in landPos);
            SoundManager.StartOneShotEvent(landBirdSound, in landPos);

            DebugLog($"[BirdKick] Land bird={GetBirdId(bird)} mode={landMode} desired=({desiredLand.x:0.##},{desiredLand.y:0.##}) final=({landPos.x:0.##},{landPos.y:0.##},{landPos.z:0.##})");
            StartBirdFlee(bird, state.SavedFlags, fromKickPosition: false);
        }

        /// <summary>
        /// Prefer the visual touchdown if it sits on navmesh; otherwise sample
        /// around that point, then a same-distance cone along the kick direction
        /// (vanilla animals never pick off-mesh targets — same GetNavigationMeshForPosition gate).
        /// </summary>
        private bool TryResolveLandingOnNavMesh(Vec3 desiredLand, Vec3 kickOrigin, out Vec3 landPos, out string mode)
        {
            Scene scene = Mission.Scene;
            // A walkable face is not enough: this scene has broken navmesh pockets,
            // and teleporting the agent onto a face disconnected from where it stood
            // is the crash class behind every 0x6d3a15 CTD. Require a path from the
            // kick spot (known-good mesh, the agent stood there) to the candidate.
            WorldPosition kickWp = new WorldPosition(scene, kickOrigin);
            if (IsLandable(scene, in kickWp, desiredLand))
            {
                landPos = SnapToGround(desiredLand);
                mode = "exact";
                return true;
            }

            // 1) Spiral around where the mesh visually hit the ground.
            if (TryFindWalkableNear(scene, in kickWp, desiredLand, LandNavSearchRadiusMax, out landPos))
            {
                mode = "nearTouchdown";
                return true;
            }

            // 2) Same distance from kick origin, cone toward the flight direction.
            Vec2 flight = (desiredLand - kickOrigin).AsVec2;
            float flightDist = flight.Length;
            if (flightDist > 0.5f)
            {
                Vec2 flightDir = flight * (1f / flightDist);
                float halfRad = LandConeHalfAngleDegrees * ((float)System.Math.PI / 180f);
                for (int i = 0; i < LandConeAngleSteps; i++)
                {
                    float t = LandConeAngleSteps == 1 ? 0f : i / (float)(LandConeAngleSteps - 1);
                    float angle = MathF.Lerp(-halfRad, halfRad, t);
                    float cos = MathF.Cos(angle);
                    float sin = MathF.Sin(angle);
                    Vec2 dir = new Vec2(flightDir.x * cos - flightDir.y * sin, flightDir.x * sin + flightDir.y * cos);
                    Vec3 candidate = kickOrigin + new Vec3(dir.x * flightDist, dir.y * flightDist, 0f);
                    candidate = SnapToGround(candidate);
                    if (IsLandable(scene, in kickWp, candidate))
                    {
                        landPos = candidate;
                        mode = "sameDistCone";
                        return true;
                    }
                }
            }

            // 3) Vanilla helper as last nearby search (falls back to its center if none).
            Vec3 randomNear = Mission.GetRandomPositionAroundPoint(desiredLand, LandNavSearchStep, LandNavSearchRadiusMax, nearFirst: true);
            randomNear = SnapToGround(randomNear);
            if ((randomNear - desiredLand).AsVec2.LengthSquared > 0.01f && IsLandable(scene, in kickWp, randomNear))
            {
                landPos = randomNear;
                mode = "randomAroundTouchdown";
                return true;
            }

            landPos = desiredLand;
            mode = "none";
            return false;
        }

        private static bool TryFindWalkableNear(Scene scene, in WorldPosition pathOrigin, Vec3 center, float maxRadius, out Vec3 found)
        {
            for (float radius = LandNavSearchStep; radius <= maxRadius + 0.001f; radius += LandNavSearchStep)
            {
                for (int i = 0; i < LandNavSearchAngles; i++)
                {
                    float angle = (float)System.Math.PI * 2f * i / LandNavSearchAngles;
                    Vec3 candidate = center + new Vec3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f);
                    candidate = SnapToGround(candidate);
                    if (IsLandable(scene, in pathOrigin, candidate))
                    {
                        found = candidate;
                        return true;
                    }
                }
            }

            found = center;
            return false;
        }

        /// <summary>
        /// Walkable navmesh AND path-connected to where the agent was standing
        /// when kicked — rejects isolated/broken navmesh islands as teleport targets.
        /// </summary>
        private static bool IsLandable(Scene scene, in WorldPosition pathOrigin, Vec3 candidate)
        {
            if (!IsWalkableNavMesh(scene, candidate))
            {
                return false;
            }

            WorldPosition candidateWp = new WorldPosition(scene, candidate);
            if (candidateWp.GetNavMesh() == System.UIntPtr.Zero)
            {
                return false;
            }

            return scene.DoesPathExistBetweenPositions(pathOrigin, candidateWp);
        }

        private static bool IsWalkableNavMesh(Scene scene, Vec3 position)
        {
            return scene.GetNavigationMeshForPosition(in position) != System.UIntPtr.Zero;
        }

        private static Vec3 SnapToGround(Vec3 position)
        {
            float groundZ = position.z;
            if (!Mission.Current.Scene.GetHeightAtPoint(position.AsVec2, BodyFlags.CommonCollisionExcludeFlagsForAgent, ref groundZ))
            {
                groundZ = Mission.Current.Scene.GetGroundHeightAtPosition(position);
            }

            position.z = groundZ + GroundClearance;
            return position;
        }

        private static void ApplyFlightVisual(FlyingBirdState state, Vec3 simPos, bool trace = false)
        {
            // The hidden agent is NOT moved during flight. Even a navmesh-guarded
            // per-tick teleport CTD'd the engine AI update once the agent reached
            // the edge of a navmesh hole (confirmed: d3-skipNoNavmesh fired, crash
            // followed anyway). The agent waits at the launch spot; only LandBird
            // teleports it, once, onto validated navmesh.
            if (state.VisualEntity != null)
            {
                if (trace)
                {
                    DebugLog($"[BirdKick] TRACE d1-entityFrame t={state.Elapsed:0.###}");
                }

                Mat3 rotation = state.VisualBaseRotation;
                rotation.RotateAboutSide(state.TumbleAngle);
                MatrixFrame frame = new MatrixFrame(rotation, simPos);
                state.VisualEntity.SetGlobalFrame(in frame);
            }
        }

        private static GameEntity? TryCreateFlightVisual(Scene scene, string meshName, Mat3 rotation, Vec3 origin)
        {
            if (string.IsNullOrEmpty(meshName))
            {
                DebugLog("[BirdKick] Flight visual skipped: empty mesh name");
                return null;
            }

            MetaMesh mesh = MetaMesh.GetCopy(meshName, showErrors: false, mayReturnNull: true);
            if (mesh == null || !mesh.IsValid)
            {
                DebugLog($"[BirdKick] Flight visual skipped: MetaMesh.GetCopy failed for '{meshName}'");
                return null;
            }

            GameEntity visual = GameEntity.CreateEmpty(scene, isModifiableFromEditor: false, createPhysics: false, callScriptCallbacks: false);
            // CTD root cause: default mobility is static, and moving a static
            // entity every frame corrupts the engine's scene structures -> random
            // AV in TaleWorlds.Native (same offset 0x6d3a15, 5x) between mission
            // ticks. Vanilla sets Dynamic on every runtime-moved entity.
            visual.SetMobility(GameEntity.Mobility.Dynamic);
            visual.AddMultiMesh(mesh);
            MatrixFrame frame = new MatrixFrame(rotation, origin);
            visual.SetGlobalFrame(in frame);
            return visual;
        }

        private static void ReleaseFlightAttachment(Agent? bird, FlyingBirdState state, bool removeEntity)
        {
            if (removeEntity && state.VisualEntity != null)
            {
                state.VisualEntity.Remove(0);
                state.VisualEntity = null;
            }

            if (bird != null && bird.IsActive())
            {
                bird.SetRenderCheckEnabled(value: true);
                if (bird.AgentVisuals != null && bird.AgentVisuals.IsValid())
                {
                    bird.AgentVisuals.SetVisible(true);
                }
            }
        }

        private static string GetBirdMeshName(Agent bird)
        {
            ItemObject? item = bird.SpawnEquipment?[EquipmentIndex.ArmorItemEndSlot].Item;
            if (item != null && !string.IsNullOrEmpty(item.MultiMeshName))
            {
                return item.MultiMeshName;
            }

            string className = bird.GetSoundAndCollisionInfoClassName();
            if (!string.IsNullOrEmpty(className) && className.IndexOf("goose", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "goose_model_a";
            }

            return "chicken_mesh";
        }

        private void TriggerCrowdReactions(Vec3 kickPos, Agent kickedBird)
        {
            int nearbyBirds = 0;
            int nearbyHumans = 0;
            AgentProximityMap.ProximityMapSearchStruct birdSearch = AgentProximityMap.BeginSearch(Mission, kickPos.AsVec2, NearbyBirdPanicRadius);
            while (birdSearch.LastFoundAgent != null)
            {
                Agent agent = birdSearch.LastFoundAgent;
                AgentProximityMap.FindNext(Mission, ref birdSearch);
                if (agent == kickedBird || !IsKickableBird(agent) || !agent.IsActive() || _flyingBirds.ContainsKey(agent))
                {
                    continue;
                }

                if (IsOnBirdPanicCooldown(agent))
                {
                    continue;
                }

                StartBirdFlee(agent, agent.GetAgentFlags(), fromKickPosition: true);
                nearbyBirds++;
            }

            AgentProximityMap.ProximityMapSearchStruct humanSearch = AgentProximityMap.BeginSearch(Mission, kickPos.AsVec2, NearbyHumanAweRadius);
            while (humanSearch.LastFoundAgent != null)
            {
                Agent agent = humanSearch.LastFoundAgent;
                AgentProximityMap.FindNext(Mission, ref humanSearch);
                if (!TryApplyHumanAwe(agent, kickPos))
                {
                    continue;
                }

                nearbyHumans++;
            }

            DebugLog($"[BirdKick] Crowd reactions birds={nearbyBirds} humans={nearbyHumans} at=({kickPos.x:0.##},{kickPos.y:0.##})");
        }

        private bool TryApplyHumanAwe(Agent agent, Vec3 kickPos)
        {
            if (agent == null || !agent.IsActive() || !agent.IsHuman || !agent.IsAIControlled || agent == Agent.Main)
            {
                return false;
            }

            if (IsHumanCurrentlyAwed(agent))
            {
                return false;
            }

            agent.SetLookAgent(Agent.Main);

            // Guards/soldiers: no scare panic / alarm. Angry glare, or random laugh.
            if (IsMilitaryWitness(agent))
            {
                bool laughs = MBRandom.RandomFloat < HumanLaughChance;
                if (laughs)
                {
                    ActionIndexCache laugh = MBRandom.RandomFloat < 0.5f
                        ? ActLaughConversation
                        : ActionIndexCache.act_cheer_1;
                    agent.SetActionChannel(1, in laugh, ignorePriority: false, (AnimFlags)0uL, 0f, 1f, -0.2f, 0.4f, MBRandom.RandomFloat);
                    agent.MakeVoice(SkinVoiceManager.VoiceType.Victory, SkinVoiceManager.CombatVoiceNetworkPredictionType.NoPrediction);
                }
                else
                {
                    ActionIndexCache angry = MBRandom.RandomFloat < 0.5f
                        ? ActThreatConversation
                        : ActNegativeConversation;
                    agent.SetActionChannel(1, in angry, ignorePriority: false, (AnimFlags)0uL, 0f, 1f, -0.2f, 0.4f, MBRandom.RandomFloat);
                    agent.MakeVoice(SkinVoiceManager.VoiceType.Yell, SkinVoiceManager.CombatVoiceNetworkPredictionType.NoPrediction);
                }

                _awedHumansUntil[agent] = Mission.CurrentTime + HumanAweDurationSeconds;
                DebugLog($"[BirdKick] Human military reaction agent={agent.Name} mode={(laughs ? "laugh" : "angry")} until={_awedHumansUntil[agent]:0.##}");
                return true;
            }

            // Civilians: upright surprise / disapproval — not act_scared_* (that
            // cower looks like bowing to the ground). No alarm group either.
            ActionIndexCache civilianReact = MBRandom.RandomFloat < 0.5f
                ? ActWonderingConversation
                : ActUnknownConversation;
            agent.SetActionChannel(1, in civilianReact, ignorePriority: false, (AnimFlags)0uL, 0f, 1f, -0.2f, 0.4f, MBRandom.RandomFloat);
            agent.MakeVoice(SkinVoiceManager.VoiceType.Stun, SkinVoiceManager.CombatVoiceNetworkPredictionType.NoPrediction);

            _awedHumansUntil[agent] = Mission.CurrentTime + HumanAweDurationSeconds;
            DebugLog($"[BirdKick] Human civilian reaction agent={agent.Name} until={_awedHumansUntil[agent]:0.##}");
            return true;
        }

        private static bool IsMilitaryWitness(Agent agent)
        {
            if (agent.Character is not CharacterObject character)
            {
                return false;
            }

            Occupation occupation = character.Occupation;
            return occupation == Occupation.Soldier
                || occupation == Occupation.Guard
                || occupation == Occupation.PrisonGuard
                || occupation == Occupation.CaravanGuard
                || occupation == Occupation.Mercenary;
        }

        private void TickHumanAweRestore()
        {
            if (_awedHumansUntil.Count == 0)
            {
                return;
            }

            float now = Mission.CurrentTime;
            List<Agent>? done = null;
            foreach (KeyValuePair<Agent, float> pair in _awedHumansUntil)
            {
                Agent agent = pair.Key;
                if (!agent.IsActive() || now >= pair.Value)
                {
                    done ??= new List<Agent>();
                    done.Add(agent);
                }
            }

            if (done == null)
            {
                return;
            }

            for (int i = 0; i < done.Count; i++)
            {
                RestoreHumanAwe(done[i]);
            }
        }

        private void RestoreHumanAwe(Agent agent)
        {
            _awedHumansUntil.Remove(agent);
            if (agent == null || !agent.IsActive())
            {
                return;
            }

            // SetLookAgent(Main) was left forever and kept them locked on the player.
            agent.ResetLookAgent();
            agent.SetActionChannel(1, in ActionIndexCache.act_none, ignorePriority: true, (AnimFlags)0uL);
            DebugLog($"[BirdKick] Human awe restore agent={agent.Name}");
        }

        private void StartBirdFlee(Agent bird, AgentFlag savedFlags, bool fromKickPosition)
        {
            if (bird == null || !bird.IsActive() || _flyingBirds.ContainsKey(bird))
            {
                return;
            }

            // Kill wander/peck idle: no CanWander, clear any prior scripted move,
            // then force a run (NeverSlowDown) to an on-mesh point away from the player.
            // Other mods may have paused animal AI — unpause or SetScriptedPosition
            // is accepted in logs while the bird stands still.
            bird.SetIsAIPaused(false);
            bird.DisableScriptedMovement();
            bird.ClearTargetFrame();
            bird.SetAgentFlags(bird.GetAgentFlags() & ~AgentFlag.CanWander);
            bird.SetMaximumSpeedLimit(FleeSpeedMultiplier, isMultiplier: true);

            if (!TryPickFleeTarget(bird, out Vec3 fleeOrigin, out WorldPosition fleePos))
            {
                DebugLog($"[BirdKick] Flee skipped (no navmesh target) bird={GetBirdId(bird)}");
                // Still keep wander off briefly so it does not immediately peck.
                bool hadWanderFallback = (savedFlags & AgentFlag.CanWander) != 0;
                _fleeingBirds[bird] = new FleeingBirdState
                {
                    SavedFlags = savedFlags,
                    EndsAt = Mission.CurrentTime + FleeDurationSeconds,
                    HadWander = hadWanderFallback
                };
                _birdPanicCooldownUntil[bird] = Mission.CurrentTime + BirdPanicCooldownSeconds;
                PlayBirdPanicSound(bird, bird.Position);
                return;
            }

            bird.SetScriptedPosition(
                ref fleePos,
                addHumanLikeDelay: false,
                Agent.AIScriptedFrameFlags.GoToPosition | Agent.AIScriptedFrameFlags.NeverSlowDown);

            bool hadWander = (savedFlags & AgentFlag.CanWander) != 0;
            _fleeingBirds[bird] = new FleeingBirdState
            {
                SavedFlags = savedFlags,
                EndsAt = Mission.CurrentTime + FleeDurationSeconds,
                HadWander = hadWander
            };
            _birdPanicCooldownUntil[bird] = Mission.CurrentTime + BirdPanicCooldownSeconds;

            PlayBirdPanicSound(bird, bird.Position);
            DebugLog($"[BirdKick] Flee bird={GetBirdId(bird)} fromKick={fromKickPosition} aiControlled={bird.IsAIControlled} run=NeverSlowDown target=({fleeOrigin.x:0.##},{fleeOrigin.y:0.##})");
        }

        private static void PlayBirdPanicSound(Agent bird, Vec3 pos)
        {
            SoundManager.StartOneShotEvent(GetBirdAirSoundEvent(bird), in pos);
        }

        private bool TryPickFleeTarget(Agent bird, out Vec3 fleeOrigin, out WorldPosition fleePos)
        {
            Agent main = Agent.Main;
            Vec2 away = (bird.Position - (main?.Position ?? bird.Position)).AsVec2;
            if (away.LengthSquared < 0.0001f)
            {
                away = bird.GetMovementDirection();
            }

            if (away.LengthSquared < 0.0001f)
            {
                away = Vec2.Forward;
            }

            away = away.Normalized();
            Scene scene = Mission.Scene;
            WorldPosition birdWp = bird.GetWorldPosition();

            // Prefer straight away from the kicker at several distances / slight angles.
            float[] distances = { FleeDistanceMax, (FleeDistanceMin + FleeDistanceMax) * 0.5f, FleeDistanceMin };
            float[] angleOffsets = { 0f, 0.35f, -0.35f, 0.7f, -0.7f, 1.1f, -1.1f };
            for (int d = 0; d < distances.Length; d++)
            {
                for (int a = 0; a < angleOffsets.Length; a++)
                {
                    float angle = angleOffsets[a];
                    float cos = MathF.Cos(angle);
                    float sin = MathF.Sin(angle);
                    Vec2 dir = new Vec2(away.x * cos - away.y * sin, away.x * sin + away.y * cos);
                    Vec3 candidate = bird.Position + new Vec3(dir.x * distances[d], dir.y * distances[d], 0f);
                    candidate = SnapToGround(candidate);
                    if (!IsWalkableNavMesh(scene, candidate))
                    {
                        continue;
                    }

                    WorldPosition wp = new WorldPosition(scene, candidate);
                    if (wp.GetNavMesh() == System.UIntPtr.Zero)
                    {
                        continue;
                    }

                    // An on-mesh endpoint is not enough: the run itself must not
                    // path into the scene's broken navmesh pockets. Same gate
                    // vanilla MovementOrder uses before scripting a destination.
                    if (!scene.DoesPathExistBetweenPositions(birdWp, wp))
                    {
                        continue;
                    }

                    fleeOrigin = candidate;
                    fleePos = wp;
                    return true;
                }
            }

            // Vanilla on-mesh sampler as last resort.
            Vec3 random = Mission.GetRandomPositionAroundPoint(bird.Position, FleeDistanceMin, FleeDistanceMax, nearFirst: false);
            random = SnapToGround(random);
            if (IsWalkableNavMesh(scene, random) && (random - bird.Position).AsVec2.LengthSquared > 1f)
            {
                WorldPosition wp = new WorldPosition(scene, random);
                if (wp.GetNavMesh() != System.UIntPtr.Zero && scene.DoesPathExistBetweenPositions(birdWp, wp))
                {
                    fleeOrigin = random;
                    fleePos = wp;
                    return true;
                }
            }

            fleeOrigin = bird.Position;
            fleePos = default;
            return false;
        }

        private void RestoreFleeingBird(Agent bird)
        {
            if (!_fleeingBirds.TryGetValue(bird, out FleeingBirdState state))
            {
                return;
            }

            _fleeingBirds.Remove(bird);
            if (bird == null || !bird.IsActive())
            {
                return;
            }

            bird.DisableScriptedMovement();
            bird.ClearTargetFrame();
            bird.SetAgentFlags(state.SavedFlags);
            bird.SetMaximumSpeedLimit(-1f, isMultiplier: false);
            DebugLog($"[BirdKick] Flee restore bird={GetBirdId(bird)} wander={state.HadWander}");
        }

        private bool IsOnBirdPanicCooldown(Agent bird)
        {
            return _birdPanicCooldownUntil.TryGetValue(bird, out float until) && Mission.CurrentTime < until;
        }

        private bool IsHumanCurrentlyAwed(Agent agent)
        {
            return _awedHumansUntil.TryGetValue(agent, out float until) && Mission.CurrentTime < until;
        }

        private static bool IsMainKickAttacker(Agent attacker)
        {
            return attacker != null && attacker == Agent.Main && attacker.IsActive();
        }

        private static bool IsAgentPerformingKick(Agent agent)
        {
            return IsKickActionType(agent.GetCurrentActionType(0)) || IsKickActionType(agent.GetCurrentActionType(1));
        }

        private static bool IsKickActionType(Agent.ActionCodeType actionType)
        {
            int value = (int)actionType;
            return value >= (int)Agent.ActionCodeType.KickAllBegin && value < (int)Agent.ActionCodeType.KickAllEnd;
        }

        private static bool IsKickableBird(Agent agent)
        {
            if (agent == null || !agent.IsActive() || agent.SpawnEquipment == null)
            {
                return false;
            }

            ItemObject item = agent.SpawnEquipment[EquipmentIndex.ArmorItemEndSlot].Item;
            if (item == null)
            {
                return false;
            }

            string id = item.StringId;
            return id == "chicken" || id == "goose";
        }

        private static string GetBirdAirSoundEvent(Agent bird)
        {
            return IsGoose(bird) ? GooseAirSoundEvent : ChickenAirSoundEvent;
        }

        private static string GetBirdLandSoundEvent(Agent bird)
        {
            return IsGoose(bird) ? GooseLandSoundEvent : ChickenLandSoundEvent;
        }

        private static bool IsGoose(Agent bird)
        {
            string className = bird.GetSoundAndCollisionInfoClassName();
            if (!string.IsNullOrEmpty(className) && className.IndexOf("goose", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            ItemObject? item = bird.SpawnEquipment?[EquipmentIndex.ArmorItemEndSlot].Item;
            return item != null && item.StringId == "goose";
        }

        private static string GetBirdId(Agent bird)
        {
            ItemObject? item = bird.SpawnEquipment?[EquipmentIndex.ArmorItemEndSlot].Item;
            return $"{item?.StringId ?? "?"}#{bird.Index}";
        }

        private static void DebugLog(string message)
        {
            if (EnableDebugLogs)
            {
                KtcLogger.Debug(message);
            }
        }
    }
}
