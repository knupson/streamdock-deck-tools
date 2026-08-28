// Runner de asserts mínimo. El repo no tiene framework de tests y no puede
// tener uno: no hay NuGet. Esto es lo que hace falta y nada más.

using System;
using System.Collections.Generic;

namespace StreamDeckAudio
{
    internal static class Check
    {
        private static readonly List<string> Failures = new List<string>();
        private static int _total;

        public static void Equal<T>(string name, T expected, T actual)
        {
            _total++;
            if (Equals(expected, actual)) { Console.WriteLine("  ok    " + name); return; }
            Failures.Add(name + ": esperaba <" + expected + ">, vino <" + actual + ">");
            Console.WriteLine("  FALLA " + name);
        }

        public static void True(string name, bool condition)
        {
            Equal(name, true, condition);
        }

        public static void Throws(string name, Action work)
        {
            _total++;
            try
            {
                work();
                Failures.Add(name + ": esperaba una excepcion, no hubo");
                Console.WriteLine("  FALLA " + name);
            }
            catch (Exception)
            {
                Console.WriteLine("  ok    " + name);
            }
        }

        public static int Report()
        {
            Console.WriteLine();
            Console.WriteLine(_total - Failures.Count + "/" + _total + " pasaron");
            foreach (string failure in Failures) Console.WriteLine("  " + failure);
            return Failures.Count == 0 ? 0 : 1;
        }
    }
}
