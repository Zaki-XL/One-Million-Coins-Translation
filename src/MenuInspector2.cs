using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace TranslationMod
{
    public static class MenuInspector2
    {
        public static void Inspect(Assembly asm, string modDir)
        {
            Type mui = asm.GetType("GoldDigger.MainMenuUI");
            if (mui == null) return;

            MethodInfo m = mui.GetMethod("Draw", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (m == null) return;

            MethodBody b = m.GetMethodBody();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== MainMenuUI.Draw FULL CALLS ===");
            byte[] il = b.GetILAsByteArray();
            for (int i = 0; i < il.Length; i++)
            {
                if (il[i] == 0x72 && i + 4 < il.Length)
                {
                    int token = BitConverter.ToInt32(il, i + 1);
                    try { sb.AppendLine("LDSTR: " + m.Module.ResolveString(token)); } catch { }
                }
                else if ((il[i] == 0x28 || il[i] == 0x6F) && i + 4 < il.Length)
                {
                    int token = BitConverter.ToInt32(il, i + 1);
                    try {
                        MethodBase mb = m.Module.ResolveMethod(token);
                        sb.AppendLine("CALL: " + mb.DeclaringType.FullName + "." + mb.Name + " (" + mb.ToString() + ")");
                    } catch { }
                }
            }
            File.WriteAllText(Path.Combine(modDir, "main_menu_full_calls.txt"), sb.ToString(), Encoding.UTF8);
        }
    }
}
