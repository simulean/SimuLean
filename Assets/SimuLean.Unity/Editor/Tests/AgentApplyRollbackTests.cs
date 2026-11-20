using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// A refused <c>simulean_apply</c> that had added teams rolled back with
    /// "Child GameObject 'MembersPreview' was not registered into undo system and became dangling" — the members' preview
    /// is built outside the undo system under a team the batch had recorded. The rollback removes the previews first.
    /// </summary>
    public class AgentApplyRollbackTests : SceneTestFixture
    {
        private const string Root = "RollbackModel";
        protected override string RootName => Root;

        private static List<GameObject> Previews()
            => UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>()
                        .Where(g => g != null && g.name == TeamMemberPreviews.Child && !EditorUtility.IsPersistent(g)).ToList();

        [Test]
        public void ARefusedApplyWithTeamsRollsBackWithoutADanglingPreview()
        {
            // a team placed before the batch keeps its preview through the rollback
            SimuLeanCommands.AddTeam("KeptTeam", 1, "Straight", 2, 0, 2, Root, "");
            TeamMemberPreviews.EnsureAll();
            Assert.IsNotNull(GameObject.Find("KeptTeam").transform.Find(TeamMemberPreviews.Child), "the kept team has its preview");

            var logged = new List<string>();
            Application.LogCallback capture = (m, s, t) => { if (t != LogType.Log) logged.Add(m); };
            Application.logMessageReceived += capture;
            Exception refused;
            try
            {
                refused = Assert.Catch(() => SimuLeanCommands.Apply(@"[
                    {""op"": ""add_team"", ""name"": ""BatchTeamA"", ""members"": 2, ""travel"": ""Straight"", ""parent"": """ + Root + @"""},
                    {""op"": ""add_team"", ""name"": ""BatchTeamB"", ""members"": 1, ""travel"": ""Straight"", ""parent"": """ + Root + @"""},
                    {""op"": ""set"", ""object"": ""MissingObject"", ""key"": ""capacity"", ""value"": 3}]"));
            }
            finally { Application.logMessageReceived -= capture; }

            StringAssert.Contains("nothing was changed", refused.Message);
            StringAssert.DoesNotContain("dangling", refused.Message, "no undo warning in the answer");
            Assert.IsFalse(logged.Any(m => m.Contains("dangling")), "no undo warning logged: " + string.Join(" | ", logged));
            Assert.IsNull(GameObject.Find("BatchTeamA"), "the batch's teams are gone");
            Assert.IsNull(GameObject.Find("BatchTeamB"));
            var previews = Previews();
            Assert.AreEqual(1, previews.Count, "no preview left over: " + string.Join(", ", previews.Select(p => SimuLeanCommands.PathOf(p.transform))));
            Assert.AreEqual("KeptTeam", previews[0].transform.parent.name, "the kept team's preview, rebuilt");
        }
    }
}
