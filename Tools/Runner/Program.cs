// The out-of-editor runner `simulean`: see RunnerCli. Ctrl+C cancels a running study (the answer then
// holds the runs done so far).
using System;
using System.Threading;

static class Program
{
    static int Main(string[] args)
    {
        // UTF-8 both ways: the editor reads and writes the runner's standard streams as UTF-8, and the console's code page
        // (e.g. cp850 on some Windows locales) turned every non-ASCII text — a scenario's " · ", an element named "Café", a
        // "→" in a waiting list — into U+FFFD on its way back.
        Console.InputEncoding = new System.Text.UTF8Encoding(false);
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        using (var cancel = new CancellationTokenSource())
        {
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cancel.Cancel(); };
            return SimuLean.Runner.RunnerCli.Run(args, Console.In, Console.Out, Console.Error, cancel.Token);
        }
    }
}
