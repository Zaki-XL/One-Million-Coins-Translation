using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using HarmonyLib;

namespace Doorstop
{
    public static class Entrypoint
    {
        public static void Start()
        {
            try
            {
                TranslationMod.Loader.EarlyInitialize();
            }
            catch (Exception ex)
            {
                File.AppendAllText("doorstop_err.log", "[Doorstop Error] " + ex.ToString() + Environment.NewLine);
            }
        }
    }
}

namespace TranslationMod
{
    public static class Loader
    {
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int AddFontResourceEx(string lpszFilename, uint fl, IntPtr pdv);
        private const uint FR_PRIVATE = 0x10;

        public static string ModDir { get; private set; }
        public static string LogPath { get; private set; }
        public static string CurrentLanguage { get; private set; }

        public static Dictionary<string, string> ItemNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static Dictionary<string, string> ItemDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static Dictionary<string, string> UpgradeDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static Dictionary<string, string> SkillNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static Dictionary<string, string> SkillDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static Dictionary<string, string> BranchNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static Dictionary<string, string> BranchDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static Dictionary<string, string> UiTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static Font CustomFontRegular;
        private static bool _engineInitialized = false;
        private static bool _assemblyHooked = false;

        public static void EarlyInitialize()
        {
            string gameDir = AppDomain.CurrentDomain.BaseDirectory;
            ModDir = Path.Combine(gameDir, "Translation");
            LogPath = Path.Combine(ModDir, "translation.log");
            CurrentLanguage = "ja";

            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

            try { File.WriteAllText(LogPath, string.Format("[{0:HH:mm:ss}] === 翻訳ローダー起動 ===" + Environment.NewLine, DateTime.Now)); } catch { }

            LoadConfigurations();

            AppDomain.CurrentDomain.AssemblyLoad += (s, a) => {
                if (a.LoadedAssembly.GetName().Name == "Assembly-CSharp") SetupHooks(a.LoadedAssembly);
            };

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "Assembly-CSharp") { SetupHooks(asm); break; }
            }

            Log("初期化完了: ロード待機中...");
        }

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            string assemblyName = new AssemblyName(args.Name).Name;
            string candidate = Path.Combine(ModDir, assemblyName + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        }

        public static void Log(string message)
        {
            try { File.AppendAllText(LogPath, string.Format("[{0:HH:mm:ss}] {1}" + Environment.NewLine, DateTime.Now, message)); } catch { }
        }

        private static void LoadConfigurations()
        {
            try
            {
                string localesDir = Path.Combine(ModDir, "locales");
                string configPath = Path.Combine(localesDir, "config.json");

                if (File.Exists(configPath))
                {
                    string cfg = File.ReadAllText(configPath);
                    int idx = cfg.IndexOf("\"language\"");
                    if (idx != -1)
                    {
                        int q1 = cfg.IndexOf('"', cfg.IndexOf(':', idx));
                        int q2 = cfg.IndexOf('"', q1 + 1);
                        if (q1 != -1 && q2 != -1) CurrentLanguage = cfg.Substring(q1 + 1, q2 - q1 - 1).Trim();
                    }
                }

                Log("選択言語: " + CurrentLanguage);
                string langFile = Path.Combine(localesDir, CurrentLanguage + ".json");
                if (!File.Exists(langFile)) langFile = Path.Combine(localesDir, "ja.json");

                if (File.Exists(langFile))
                {
                    ParseDictionary(File.ReadAllText(langFile));
                    Log(string.Format("辞書読込: アイテム({0}), スキル({1}), ブランチ({2}), UI({3})",
                        ItemNames.Count, SkillNames.Count, BranchNames.Count, UiTexts.Count));
                }
            }
            catch (Exception ex) { Log("設定読込エラー: " + ex); }
        }

        private static void ParseDictionary(string json)
        {
            ParseObj(json, "items", (k, n, d) => { if (n != null) ItemNames[k] = n; if (d != null) ItemDescriptions[k] = d; });
            ParseObj(json, "upgrades", (k, n, d) => { if (d != null) UpgradeDescriptions[k] = d; });
            ParseObj(json, "skills", (k, n, d) => { if (n != null) SkillNames[k] = n; if (d != null) SkillDescriptions[k] = d; });
            ParseObj(json, "branches", (k, n, d) => { if (n != null) BranchNames[k] = n; if (d != null) BranchDescriptions[k] = d; });

            int uIdx = json.IndexOf("\"ui\"");
            if (uIdx != -1)
            {
                int bStart = json.IndexOf('{', uIdx);
                if (bStart != -1)
                {
                    foreach (var raw in json.Substring(bStart).Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string line = raw.Trim();
                        if (line.StartsWith("\"") && line.Contains(":"))
                        {
                            string[] p = line.Split(new char[] { ':' }, 2);
                            if (p.Length == 2)
                            {
                                string k = p[0].Trim().Trim('"');
                                string v = ExtractVal(line);
                                if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v)) UiTexts[k] = v;
                            }
                        }
                    }
                }
            }
        }

        private static void ParseObj(string json, string section, Action<string, string, string> onEntry)
        {
            int sIdx = json.IndexOf("\"" + section + "\"");
            if (sIdx == -1) return;
            int bStart = json.IndexOf('{', sIdx);
            if (bStart == -1) return;

            string curK = null, curN = null, curD = null;
            foreach (var raw in json.Substring(bStart).Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.StartsWith("\"") && line.EndsWith("{"))
                {
                    if (curK != null) onEntry(curK, curN, curD);
                    curK = line.Split('"')[1]; curN = null; curD = null;
                }
                else if (line.Contains("\"name\"") && line.Contains(":")) curN = ExtractVal(line);
                else if (line.Contains("\"description\"") && line.Contains(":")) curD = ExtractVal(line);
                else if (line == "}," || line == "}")
                {
                    if (curK != null) { onEntry(curK, curN, curD); curK = null; }
                }
            }
        }

        private static string ExtractVal(string line)
        {
            int colon = line.IndexOf(':');
            int q1 = line.IndexOf('"', colon);
            int q2 = line.LastIndexOf('"');
            return (q1 != -1 && q2 > q1) ? line.Substring(q1 + 1, q2 - q1 - 1).Replace("\\\"", "\"").Replace("\\\\", "\\") : null;
        }

        private static void SetupHooks(Assembly asm)
        {
            if (_assemblyHooked) return;
            _assemblyHooked = true;

            try
            {
                Log("Assembly-CSharp ロード検知。フックを適用中...");
                Harmony harmony = new Harmony("com.translator.onemillioncoins");

                Type mainUiType = asm.GetType("GoldDigger.MainMenuUI");
                if (mainUiType != null)
                {
                    MethodInfo mStart = AccessTools.Method(mainUiType, "Start");
                    if (mStart != null) harmony.Patch(mStart, new HarmonyMethod(AccessTools.Method(typeof(Loader), "MainMenuUI_Start_Prefix")), null);

                    MethodInfo mOnGUI = AccessTools.Method(mainUiType, "OnGUI");
                    if (mOnGUI != null) harmony.Patch(mOnGUI, new HarmonyMethod(AccessTools.Method(typeof(Loader), "MainMenuUI_OnGUI_Prefix")), null);

                    MethodInfo mButton = AccessTools.Method(mainUiType, "MenuButton", new Type[] { typeof(Rect), typeof(string), typeof(bool) });
                    if (mButton != null) harmony.Patch(mButton, new HarmonyMethod(AccessTools.Method(typeof(Loader), "MenuButton_Prefix")), null);
                }

                Type itemCatType = asm.GetType("GoldDigger.ItemCatalog");
                if (itemCatType != null)
                {
                    MethodInfo mForFloor = AccessTools.Method(itemCatType, "ForFloor", new Type[] { typeof(int) });
                    if (mForFloor != null) harmony.Patch(mForFloor, null, new HarmonyMethod(AccessTools.Method(typeof(Loader), "ItemCatalog_ForFloor_Postfix")));
                }

                Type upCatType = asm.GetType("GoldDigger.UpgradeCatalog");
                if (upCatType != null)
                {
                    MethodInfo mCreateDefaults = AccessTools.Method(upCatType, "CreateDefaults");
                    if (mCreateDefaults != null) harmony.Patch(mCreateDefaults, null, new HarmonyMethod(AccessTools.Method(typeof(Loader), "UpgradeCatalog_CreateDefaults_Postfix")));
                }

                Type vaultUiType = asm.GetType("GoldDigger.VaultUI");
                if (vaultUiType != null)
                {
                    MethodInfo mEnsure = AccessTools.Method(vaultUiType, "Ensure");
                    if (mEnsure != null) harmony.Patch(mEnsure, null, new HarmonyMethod(AccessTools.Method(typeof(Loader), "VaultUI_Ensure_Postfix")));
                }

                TranslateSkillCatalog(asm);
                Log("全フックおよびスキル翻訳の適用が完了しました。");
            }
            catch (Exception ex) { Log("フック適用エラー: " + ex); }
        }

        private static void TranslateSkillCatalog(Assembly asm)
        {
            try
            {
                Type skillCatType = asm.GetType("GoldDigger.SkillCatalog");
                if (skillCatType == null) return;

                FieldInfo fBNames = skillCatType.GetField("BranchNames", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                FieldInfo fBDescs = skillCatType.GetField("BranchDescriptions", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (fBNames != null)
                {
                    string[] bnames = fBNames.GetValue(null) as string[];
                    if (bnames != null)
                    {
                        for (int i = 0; i < bnames.Length; i++)
                        {
                            string tr;
                            if (BranchNames.TryGetValue(i.ToString(), out tr)) bnames[i] = tr;
                        }
                    }
                }

                if (fBDescs != null)
                {
                    string[] bdescs = fBDescs.GetValue(null) as string[];
                    if (bdescs != null)
                    {
                        for (int i = 0; i < bdescs.Length; i++)
                        {
                            string tr;
                            if (BranchDescriptions.TryGetValue(i.ToString(), out tr)) bdescs[i] = tr;
                        }
                    }
                }

                FieldInfo fAll = skillCatType.GetField("All", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (fAll != null)
                {
                    Array allSkills = fAll.GetValue(null) as Array;
                    if (allSkills != null)
                    {
                        int count = 0;
                        foreach (var skill in allSkills)
                        {
                            if (skill == null) continue;
                            Type st = skill.GetType();
                            FieldInfo fId = st.GetField("Id");
                            FieldInfo fName = st.GetField("Name");
                            FieldInfo fDesc = st.GetField("Description");

                            if (fId != null)
                            {
                                string id = fId.GetValue(skill).ToString();
                                string trN, trD;
                                if (SkillNames.TryGetValue(id, out trN) && fName != null) { fName.SetValue(skill, trN); count++; }
                                if (SkillDescriptions.TryGetValue(id, out trD) && fDesc != null) fDesc.SetValue(skill, trD);
                            }
                        }
                        Log(string.Format("SkillCatalog.All 翻訳完了: {0} 件置換", count));
                    }
                }
            }
            catch (Exception ex) { Log("SkillCatalog翻訳適用エラー: " + ex); }
        }

        public static void MainMenuUI_Start_Prefix(object __instance)
        {
            if (_engineInitialized) return;
            _engineInitialized = true;
            try { InitializeFonts(); } catch (Exception ex) { Log("フォント初期化エラー: " + ex); }
        }

        public static void MainMenuUI_OnGUI_Prefix(object __instance)
        {
            try
            {
                if (CustomFontRegular != null)
                {
                    if (GUI.skin != null)
                    {
                        GUI.skin.font = CustomFontRegular;
                    }
                    if (__instance != null)
                    {
                        Type t = __instance.GetType();
                        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                        {
                            if (f.FieldType == typeof(GUIStyle))
                            {
                                GUIStyle s = f.GetValue(__instance) as GUIStyle;
                                if (s != null)
                                {
                                    s.font = CustomFontRegular;
                                    s.fontStyle = FontStyle.Bold;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public static void MenuButton_Prefix(ref string label)
        {
            if (string.IsNullOrEmpty(label)) return;
            string tr;
            if (UiTexts.TryGetValue(label.Trim(), out tr)) label = tr;
        }

        private static void InitializeFonts()
        {
            List<string> candidates = new List<string>();
            string fontsDir = Path.Combine(ModDir, "fonts");
            string langFontsDir = Path.Combine(fontsDir, CurrentLanguage);

            foreach (var dir in new string[] { langFontsDir, fontsDir })
            {
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.*"))
                    {
                        string ext = Path.GetExtension(f).ToLower();
                        if (ext == ".ttf" || ext == ".otf")
                        {
                            if (AddFontResourceEx(f, FR_PRIVATE, IntPtr.Zero) > 0)
                            {
                                string fname = Path.GetFileNameWithoutExtension(f);
                                if (!candidates.Contains(fname)) candidates.Add(fname);
                            }
                        }
                    }
                }
            }

            // Yu Gothic UI Bold を最優先に設定
            foreach (var s in new string[] { "Yu Gothic UI Bold", "Yu Gothic UI", "Yu Gothic", "Meiryo Bold", "Meiryo" })
            {
                if (!candidates.Contains(s)) candidates.Add(s);
            }

            CustomFontRegular = Font.CreateDynamicFontFromOSFont(candidates.ToArray(), 18);
            Log("フォント適用: " + ((CustomFontRegular != null) ? CustomFontRegular.name : "null") + " (FontStyle.Bold)");
        }

        public static void VaultUI_Ensure_Postfix(object __instance)
        {
            try
            {
                if (CustomFontRegular == null) return;
                Type t = __instance.GetType();
                FieldInfo fReg = t.GetField("_regular", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                FieldInfo fBold = t.GetField("_bold", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                FieldInfo fDisp = t.GetField("_display", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (fReg != null) fReg.SetValue(__instance, CustomFontRegular);
                if (fBold != null) fBold.SetValue(__instance, CustomFontRegular);
                if (fDisp != null) fDisp.SetValue(__instance, CustomFontRegular);

                // VaultUI の各 GUIStyle も Bold に設定
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (f.FieldType == typeof(GUIStyle))
                    {
                        GUIStyle style = f.GetValue(__instance) as GUIStyle;
                        if (style != null)
                        {
                            style.font = CustomFontRegular;
                            style.fontStyle = FontStyle.Bold;
                        }
                    }
                }
            }
            catch { }
        }

        public static void ItemCatalog_ForFloor_Postfix(object __result)
        {
            try
            {
                if (__result == null) return;
                Array items = __result as Array;
                if (items == null) return;

                foreach (var item in items)
                {
                    if (item == null) continue;
                    Type it = item.GetType();
                    PropertyInfo pId = it.GetProperty("Id");
                    FieldInfo fName = it.GetField("<Name>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    FieldInfo fDesc = it.GetField("<Description>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                    if (pId != null)
                    {
                        string id = pId.GetValue(item, null).ToString();
                        string tr;
                        if (ItemNames.TryGetValue(id, out tr) && fName != null) fName.SetValue(item, tr);
                        if (ItemDescriptions.TryGetValue(id, out tr) && fDesc != null) fDesc.SetValue(item, tr);
                    }
                }
            }
            catch { }
        }

        public static void UpgradeCatalog_CreateDefaults_Postfix(object __result)
        {
            try
            {
                if (__result == null) return;
                Array upgrades = __result as Array;
                if (upgrades == null) return;

                foreach (var up in upgrades)
                {
                    if (up == null) continue;
                    Type ut = up.GetType();
                    PropertyInfo pId = ut.GetProperty("Id");
                    FieldInfo fDesc = ut.GetField("<Description>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                    if (pId != null && fDesc != null)
                    {
                        string id = pId.GetValue(up, null).ToString();
                        string tr;
                        if (UpgradeDescriptions.TryGetValue(id, out tr)) fDesc.SetValue(up, tr);
                    }
                }
            }
            catch { }
        }
    }
}
