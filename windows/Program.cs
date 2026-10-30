using System;
using System.Threading;
using System.Windows.Forms;

namespace DeckPad
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var m = new Mutex(true, "DeckPad.SingleInstance", out created))
            {
                if (!created)
                {
                    // Already running (possibly hidden in the tray): ask it to show its window.
                    try { EventWaitHandle.OpenExisting(MainForm.ShowEventName).Set(); }
                    catch (Exception) { }
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
