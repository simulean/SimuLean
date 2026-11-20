namespace SimuLean
{
    /// <summary>
    /// The view of an element built without one (headless, or in code): it shows nothing and gives its items no
    /// visual. The engine's own, so the element core does not depend on the headless build
    /// (<c>SimuLean.Headless.HeadlessVElement</c> is the factory's view, which can also log).
    /// </summary>
    internal sealed class NoView : IVElement
    {
        public void ReportState(string msg) { }

        public object GenerateItem(int type) => null;

        public void LoadItem(Item vItem) => vItem.vItem = null;

        public void UnloadItem(Item vItem) { }
    }
}
