using System.Collections.Generic;
using UnityEditor;
using UnityEditor.EditorTools;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The checks every element gets: shown with their fix in the element's inspector header and counted in
    /// the SimuLean window's Model tab. Element-specific checks live in each inspector (<c>CollectIssues</c>).
    /// </summary>
    public static class ElementChecks
    {
        public static void Collect(SElement el, List<InspectorIssue> issues, bool checkFed = true)
        {
            System.Action connect = () => SimuLeanConnectTool.Activate();
            if (el.HasOutput && el.sendTo == SElement.SendMode.Elements && !HasAny(el.nextElements))
                issues.Add(new InspectorIssue(MessageType.Warning, "It sends its items nowhere: connect a next element.", "Connect…", connect));
            if (el.sendTo == SElement.SendMode.List && el.pushToList == null)
                issues.Add(new InspectorIssue(MessageType.Error, "Send To is a list, but no list is chosen (Flow)."));
            if (el.receiveFrom == SElement.ReceiveMode.List && el.pullFromList == null)
                issues.Add(new InspectorIssue(MessageType.Error, "Receive From is a list, but no list is chosen (Flow)."));
            if (checkFed && el.HasInput && el.receiveFrom == SElement.ReceiveMode.Connections && !Fed(el))
                issues.Add(new InspectorIssue(MessageType.Info, "Nothing sends items to it yet.", "Connect…", connect));
            if (el.transportBy != null && el.transportBy.IsUsed && el.location == null)
                issues.Add(new InspectorIssue(MessageType.Error, "Its items are carried by a team, so it needs a Location node (Resources)."));
            // Reroute only for a Multi-Server's process; Before The Part only on the Multi-Server (the build refuses them elsewhere):
            // flagged with a fix rather than hidden, so a value set through the MCP is seen
            bool multiServer = el is UnityMultiServer;
            if (el is IProcessResourceUser && el.processBy != null && el.processBy.IsUsed)
            {
                var use = el.processBy;
                if (!multiServer && use.onTimeout == SimuLean.TimeoutAction.Reroute)
                    issues.Add(new InspectorIssue(MessageType.Error, "On Timeout = Reroute is for a Multi-Server's process only.", "Give Up",
                        () => { UnityEditor.Undo.RecordObject(el, "On Timeout"); use.onTimeout = SimuLean.TimeoutAction.GiveUp; EditorUtility.SetDirty(el); }));
                if (!multiServer && use.acquireBeforeEntry)
                    issues.Add(new InspectorIssue(MessageType.Error, "Before The Part is for the Multi-Server only.", "Switch off",
                        () => { UnityEditor.Undo.RecordObject(el, "Before The Part"); use.acquireBeforeEntry = false; EditorUtility.SetDirty(el); }));
            }
            if (el is ISetupUser su && su.Setup?.setupBy != null && su.Setup.setupBy.onTimeout == SimuLean.TimeoutAction.Reroute)
                issues.Add(new InspectorIssue(MessageType.Error, "A setup crew's On Timeout cannot be Reroute (Keep Waiting or Give Up).", "Give Up",
                    () => { UnityEditor.Undo.RecordObject(el, "On Timeout"); su.Setup.setupBy.onTimeout = SimuLean.TimeoutAction.GiveUp; EditorUtility.SetDirty(el); }));
        }

        /// <summary>The number of warnings and errors of an element (infos not counted).</summary>
        public static int ProblemCount(SElement el)
        {
            var list = new List<InspectorIssue>();
            Collect(el, list);
            int n = 0;
            foreach (var i in list) if (i.Severity != MessageType.Info) n++;
            return n;
        }

        private static bool HasAny(List<SElement> list)
        {
            if (list == null) return false;
            foreach (var x in list) if (x != null) return true;
            return false;
        }

        private static bool Fed(SElement el)
        {
            foreach (var c in SceneConnections.All())
                if (c.To == el && (c.Kind == ConnectionKind.Flow || c.Kind == ConnectionKind.PullList || c.Kind == ConnectionKind.CombinerListInput)) return true;
            return false;
        }
    }
}
