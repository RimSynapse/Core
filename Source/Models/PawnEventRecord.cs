using Verse;

namespace RimSynapse
{
    public class PawnEventRecord : IExposable
    {
        public string id;
        public string eventName;
        public string dateString;
        public string targetPawnName;
        public string fullLog;

        public PawnEventRecord() {}

        public PawnEventRecord(string id, string eventName, string dateString, string targetPawnName, string fullLog)
        {
            this.id = id;
            this.eventName = eventName;
            this.dateString = dateString;
            this.targetPawnName = targetPawnName;
            this.fullLog = fullLog;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref eventName, "eventName");
            Scribe_Values.Look(ref dateString, "dateString");
            Scribe_Values.Look(ref targetPawnName, "targetPawnName");
            Scribe_Values.Look(ref fullLog, "fullLog");
        }
    }
}
