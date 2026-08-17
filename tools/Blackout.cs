// Full-screen blackout overlay.
//
// One black window per monitor, above everything, dismissed by any key or click.
// It is a separate process on purpose: the plugin backend owns a single STA
// thread for COM and its event pump, and a message loop for these windows cannot
// live there. The plugin starts and kills this process to toggle the blackout.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

internal static class Blackout
{
    private static readonly List<Form> Screens = new List<Form>();
    private static bool _closing;

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();

        foreach (Screen screen in Screen.AllScreens) Screens.Add(Cover(screen));
        if (Screens.Count == 0) return;

        foreach (Form form in Screens) form.Show();

        Cursor.Hide();
        try
        {
            Application.Run(Screens[0]);
        }
        finally
        {
            Cursor.Show();
        }
    }

    private static Form Cover(Screen screen)
    {
        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = screen.Bounds,
            BackColor = Color.Black,
            ShowInTaskbar = false,
            TopMost = true,
            KeyPreview = true
        };

        form.KeyDown += delegate { DismissAll(); };
        form.MouseDown += delegate { DismissAll(); };
        form.MouseWheel += delegate { DismissAll(); };

        // Windows can hand focus elsewhere on a multi-monitor show; taking it
        // back keeps the keyboard dismissal working.
        form.Shown += delegate { form.Activate(); };

        return form;
    }

    private static void DismissAll()
    {
        if (_closing) return;
        _closing = true;

        // Close the extras first; the primary form ends Application.Run.
        for (int i = Screens.Count - 1; i >= 1; i--)
        {
            try { Screens[i].Close(); }
            catch (Exception) { /* already gone */ }
        }
        Screens[0].Close();
    }
}
