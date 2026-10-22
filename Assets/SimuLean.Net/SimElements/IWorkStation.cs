namespace SimuLean
{
    /// <summary>
    /// Default itnerface for every workstation type.
    /// </summary>
    public interface IWorkStation
    {
        /// <summary> 
        /// Starts processing the item. If not possible, server remains blocked until it is.
        /// </summary>
        /// <param name="theProcess"></param>
        void StartProcessing(ServerProcess theProcess);


        /// <summary>
        /// Completes pending process and tries to send the item. If not possible, server remains blocked.
        /// </summary>
        /// <param name="theProcess"></param>
        void CompleteServerProcess(ServerProcess theProcess);
        string GetName();
    }
}
