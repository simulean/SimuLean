namespace SimuLean.Headless
{
    /// <summary>
    /// The view of an element in a headless run: no visuals; optionally logs what a view would show (debugging).
    /// </summary>
    public class HeadlessVElement : IVElement
    {
        private readonly bool enableLogging;

        /// <param name="enableLogging">True: every view call is logged (<see cref="SimLog.Info"/>).</param>
        public HeadlessVElement(bool enableLogging = false)
        {
            this.enableLogging = enableLogging;
        }

        public void ReportState(string msg)
        {
            if (enableLogging) SimLog.Info($"[Headless] {msg}");
        }

        public object GenerateItem(int type) => null;

        public void LoadItem(Item vItem)
        {
            vItem.vItem = null;
            if (enableLogging) SimLog.Info($"[Headless] Item {vItem.GetId()} loaded");
        }

        public void UnloadItem(Item vItem)
        {
            if (enableLogging) SimLog.Info($"[Headless] Item {vItem.GetId()} unloaded");
        }
    }
}
