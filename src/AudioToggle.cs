// Command-line companion to the plugin, for diagnostics: it exercises the same
// audio backend without the StreamDock software in the loop.
//
//   AudioToggle.exe --list                 active outputs, * marks the default
//   AudioToggle.exe --next                 switch to the next active output
//   AudioToggle.exe --set <name substring> make a specific device the default
//   AudioToggle.exe --apps                 applications holding a session, as the
//                                          property inspector lists them

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace StreamDeckAudio
{
    internal static class Cli
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        [STAThread]
        private static int Main(string[] args)
        {
            AttachConsole(-1); // ATTACH_PARENT_PROCESS: no console window of its own
            Log.Component = "audiotoggle";

            try
            {
                List<AudioDevice> active = AudioDevices.ListActiveOutputs();
                string currentId = AudioDevices.GetDefaultOutputId();
                string command = args.Length > 0 ? args[0] : "--list";

                switch (command)
                {
                    case "--list":
                        foreach (AudioDevice device in active)
                            Console.WriteLine((device.Id == currentId ? "* " : "  ") +
                                              device.Name + "  [" + device.Id + "]");
                        return 0;

                    case "--apps":
                        foreach (AppChoice choice in AudioSessions.Applications())
                            Console.WriteLine("  " + choice.Label.PadRight(16) + "  (process: " + choice.Id + ")");
                        return 0;

                    case "--next":
                        return Switch(NextAfter(active, currentId), currentId);

                    case "--set":
                        if (args.Length < 2)
                        {
                            Console.WriteLine("--set needs a device name substring");
                            return 1;
                        }
                        return Switch(AudioDevices.FindByName(active, args[1]), currentId);

                    default:
                        Console.WriteLine("usage: AudioToggle.exe [--list | --apps | --next | --set <substring>]");
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                Log.Write("ERROR " + ex);
                return 2;
            }
        }

        private static AudioDevice NextAfter(List<AudioDevice> active, string currentId)
        {
            if (active.Count == 0) return null;
            int index = active.FindIndex(d => d.Id == currentId);
            return index < 0 ? active[0] : active[(index + 1) % active.Count];
        }

        private static int Switch(AudioDevice target, string currentId)
        {
            if (target == null)
            {
                Console.WriteLine("No matching active output.");
                return 1;
            }
            if (target.Id == currentId)
            {
                Console.WriteLine("Already default: " + target.Name);
                return 0;
            }

            AudioDevices.SetDefaultOutput(target.Id);
            Console.WriteLine("Default output -> " + target.Name);
            Log.Write("switched to " + target.Name);
            return 0;
        }
    }
}
