using TaleWorlds.MountAndBlade;

namespace LT_KickTheChicken
{
    public class SubModule : MBSubModuleBase
    {
        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            base.OnMissionBehaviorInitialize(mission);
            if (mission == null || mission.HasMissionBehavior<KickBirdMissionLogic>())
            {
                return;
            }

            mission.AddMissionBehavior(new KickBirdMissionLogic());
        }
    }
}
