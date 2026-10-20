using System;
using System.IO;
using System.Text;
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
            try { TranslationMod.Loader.EarlyInitialize(); }
            catch (Exception ex) { File.AppendAllText("doorstop_err.log", "[Doorstop Error] " + ex + Environment.NewLine); }
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
        }

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            string p = Path.Combine(ModDir, name + ".dll");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
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

                // YAML形式の辞書を最優先探索（.yaml -> .yml -> .json）
                string yamlFile = Path.Combine(localesDir, CurrentLanguage + ".yaml");
                if (!File.Exists(yamlFile)) yamlFile = Path.Combine(localesDir, CurrentLanguage + ".yml");
                if (!File.Exists(yamlFile)) yamlFile = Path.Combine(localesDir, "ja.yaml");
                if (!File.Exists(yamlFile)) yamlFile = Path.Combine(localesDir, "ja.yml");

                if (File.Exists(yamlFile))
                {
                    Log("YAML辞書読込: " + Path.GetFileName(yamlFile));
                    ParseYaml(File.ReadAllText(yamlFile, Encoding.UTF8));
                }
                else
                {
                    string jsonFile = Path.Combine(localesDir, CurrentLanguage + ".json");
                    if (!File.Exists(jsonFile)) jsonFile = Path.Combine(localesDir, "ja.json");
                    if (File.Exists(jsonFile))
                    {
                        Log("JSON辞書読込: " + Path.GetFileName(jsonFile));
                        ParseDictionary(File.ReadAllText(jsonFile, Encoding.UTF8));
                    }
                }

                Log(string.Format("辞書読込完了: アイテム({0}), スキル({1}), ブランチ({2}), UI({3})",
                    ItemNames.Count, SkillNames.Count, BranchNames.Count, UiTexts.Count));
            }
            catch (Exception ex) { Log("設定読込エラー: " + ex); }
        }

        private static string ApplyScale(string text, double scale)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (scale > 0.01 && Math.Abs(scale - 1.0) > 0.001)
            {
                int pct = (int)Math.Round(scale * 100.0);
                return string.Format("<size={0}%>{1}</size>", pct, text);
            }
            return text;
        }

        private class ParsedEntry
        {
            public string Name;
            public string Description;
            public string Text;
            public bool Enabled = true;
            public double Scale = 1.0;
            public double NameScale = 1.0;
            public double DescScale = 1.0;
        }

        private static void ParseYaml(string yaml)
        {
            string[] lines = yaml.Split(new char[] { '\r', '\n' }, StringSplitOptions.None);
            string currentSection = null;
            string currentKey = null;
            string currentBlockProp = null;
            int blockIndent = -1;
            List<string> blockLines = new List<string>();

            Dictionary<string, Dictionary<string, ParsedEntry>> sectionData = new Dictionary<string, Dictionary<string, ParsedEntry>>(StringComparer.OrdinalIgnoreCase);

            Action flushBlock = () => {
                if (currentSection != null && currentKey != null && currentBlockProp != null && blockLines.Count > 0)
                {
                    string joined = string.Join("\n", blockLines.ToArray());
                    if (sectionData.ContainsKey(currentSection) && sectionData[currentSection].ContainsKey(currentKey))
                    {
                        var ent = sectionData[currentSection][currentKey];
                        if (currentBlockProp == "description") ent.Description = joined;
                        else if (currentBlockProp == "text") ent.Text = joined;
                        else if (currentBlockProp == "name") ent.Name = joined;
                    }
                }
                blockLines.Clear();
                currentBlockProp = null;
                blockIndent = -1;
            };

            foreach (var rawLine in lines)
            {
                string line = rawLine;
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;

                int indent = 0;
                while (indent < line.Length && line[indent] == ' ') indent++;

                if (blockIndent > 0)
                {
                    if (indent >= blockIndent)
                    {
                        blockLines.Add(line.Length >= blockIndent ? line.Substring(blockIndent) : line.TrimStart());
                        continue;
                    }
                    else
                    {
                        flushBlock();
                    }
                }

                if (indent == 0 && trimmed.EndsWith(":"))
                {
                    currentSection = trimmed.Substring(0, trimmed.Length - 1).Trim().ToLower();
                    if (!sectionData.ContainsKey(currentSection)) sectionData[currentSection] = new Dictionary<string, ParsedEntry>(StringComparer.OrdinalIgnoreCase);
                    currentKey = null;
                    continue;
                }

                if (indent == 2 && currentSection != null)
                {
                    int colon = trimmed.IndexOf(':');
                    if (colon != -1)
                    {
                        string k = trimmed.Substring(0, colon).Trim().Trim('"', '\'');
                        string val = trimmed.Substring(colon + 1).Trim();
                        currentKey = k;
                        if (!sectionData[currentSection].ContainsKey(currentKey)) sectionData[currentSection][currentKey] = new ParsedEntry();

                        if (!string.IsNullOrEmpty(val))
                        {
                            if (val == "|")
                            {
                                currentBlockProp = "text";
                                blockIndent = 4;
                            }
                            else
                            {
                                string unquoted = Unquote(val);
                                sectionData[currentSection][currentKey].Text = unquoted;
                            }
                        }
                    }
                    continue;
                }

                if (indent == 4 && currentSection != null && currentKey != null)
                {
                    int colon = trimmed.IndexOf(':');
                    if (colon != -1)
                    {
                        string prop = trimmed.Substring(0, colon).Trim().ToLower();
                        string val = trimmed.Substring(colon + 1).Trim();

                        if (!sectionData[currentSection].ContainsKey(currentKey)) sectionData[currentSection][currentKey] = new ParsedEntry();
                        var ent = sectionData[currentSection][currentKey];

                        if (val == "|")
                        {
                            currentBlockProp = prop;
                            blockIndent = 6;
                        }
                        else
                        {
                            string unquoted = Unquote(val);
                            if (prop == "name") ent.Name = unquoted;
                            else if (prop == "description") ent.Description = unquoted;
                            else if (prop == "text") ent.Text = unquoted;
                            else if (prop == "enabled")
                            {
                                bool b;
                                if (bool.TryParse(unquoted, out b)) ent.Enabled = b;
                            }
                            else if (prop == "scale")
                            {
                                double s;
                                if (double.TryParse(unquoted, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out s)) ent.Scale = s;
                            }
                            else if (prop == "name_scale")
                            {
                                double s;
                                if (double.TryParse(unquoted, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out s)) ent.NameScale = s;
                            }
                            else if (prop == "desc_scale")
                            {
                                double s;
                                if (double.TryParse(unquoted, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out s)) ent.DescScale = s;
                            }
                        }
                    }
                    continue;
                }
            }

            flushBlock();

            // 辞書へのマージ
            foreach (var kvSec in sectionData)
            {
                string sec = kvSec.Key;
                foreach (var kvEnt in kvSec.Value)
                {
                    string id = kvEnt.Key;
                    ParsedEntry ent = kvEnt.Value;

                    // enabled: false のエントリは辞書登録をスキップ（英語原文を維持）
                    if (!ent.Enabled) continue;

                    double nScale = (ent.NameScale != 1.0) ? ent.NameScale : ent.Scale;
                    double dScale = (ent.DescScale != 1.0) ? ent.DescScale : ent.Scale;

                    string finalName = ApplyScale(ent.Name, nScale);
                    string finalDesc = ApplyScale(ent.Description, dScale);
                    string finalText = ApplyScale(ent.Text, ent.Scale);

                    if (sec == "skills")
                    {
                        if (finalName != null) SkillNames[id] = finalName;
                        if (finalDesc != null) SkillDescriptions[id] = finalDesc;
                    }
                    else if (sec == "items")
                    {
                        if (finalName != null) ItemNames[id] = finalName;
                        if (finalDesc != null) ItemDescriptions[id] = finalDesc;
                    }
                    else if (sec == "upgrades")
                    {
                        if (finalDesc != null) UpgradeDescriptions[id] = finalDesc;
                        else if (finalText != null) UpgradeDescriptions[id] = finalText;
                    }
                    else if (sec == "branches")
                    {
                        if (finalName != null) BranchNames[id] = finalName;
                        if (finalDesc != null) BranchDescriptions[id] = finalDesc;
                    }
                    else if (sec == "ui")
                    {
                        if (finalText != null) UiTexts[id] = finalText;
                        else if (finalName != null) UiTexts[id] = finalName;
                    }
                }
            }
        }

        private static string Unquote(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.StartsWith("\"") && s.EndsWith("\"") && s.Length >= 2)
            {
                return s.Substring(1, s.Length - 2).Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\r", "");
            }
            if (s.StartsWith("'") && s.EndsWith("'") && s.Length >= 2)
            {
                return s.Substring(1, s.Length - 2).Replace("''", "'");
            }
            return s;
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
            if (q1 != -1 && q2 > q1)
            {
                string s = line.Substring(q1 + 1, q2 - q1 - 1);
                return s.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\\"", "\"").Replace("\\\\", "\\");
            }
            return null;
        }

        private static void SetupHooks(Assembly asm)
        {
            if (_assemblyHooked) return;
            _assemblyHooked = true;

            try
            {
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

                Type vaultUiType = asm.GetType("GoldDigger.VaultUI");
                if (vaultUiType != null)
                {
                    MethodInfo mEnsure = AccessTools.Method(vaultUiType, "Ensure");
                    if (mEnsure != null) harmony.Patch(mEnsure, null, new HarmonyMethod(AccessTools.Method(typeof(Loader), "VaultUI_Ensure_Postfix")));

                    MethodInfo mActionBtn = AccessTools.Method(vaultUiType, "ActionButton", new Type[] { typeof(Rect), typeof(string), typeof(GUIStyle), typeof(bool), typeof(bool) });
                    if (mActionBtn != null) harmony.Patch(mActionBtn, new HarmonyMethod(AccessTools.Method(typeof(Loader), "ActionButton_Prefix")), null);
                }

                Type settingsType = asm.GetType("GoldDigger.SettingsPanel");
                if (settingsType != null)
                {
                    MethodInfo mWinBtn = AccessTools.Method(settingsType, "WindowModeButton");
                    if (mWinBtn != null) harmony.Patch(mWinBtn, new HarmonyMethod(AccessTools.Method(typeof(Loader), "WindowModeButton_Prefix")), null);

                    MethodInfo mVol = AccessTools.Method(settingsType, "Volume");
                    if (mVol != null) harmony.Patch(mVol, new HarmonyMethod(AccessTools.Method(typeof(Loader), "Volume_Prefix")), null);
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

                TranslateSkillCatalog(asm);
                Log("全フックおよびスキル翻訳の登録完了");
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
                                if (SkillNames.TryGetValue(id, out trN) && fName != null) fName.SetValue(skill, trN);
                                if (SkillDescriptions.TryGetValue(id, out trD) && fDesc != null)
                                {
                                    fDesc.SetValue(skill, trD.Replace("\\n", "\n").Replace("\\r", ""));
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log("SkillCatalog翻訳適用エラー: " + ex); }
        }

        public static void MainMenuUI_Start_Prefix(object __instance)
        {
            if (_engineInitialized) return;
            _engineInitialized = true;
            try { InitializeFonts(); } catch { }
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
                        if (GUI.skin.label != null) GUI.skin.label.richText = true;
                        if (GUI.skin.button != null) GUI.skin.button.richText = true;
                        if (GUI.skin.box != null) GUI.skin.box.richText = true;
                    }
                    if (__instance != null)
                    {
                        Type t = __instance.GetType();
                        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                        {
                            if (f.FieldType == typeof(GUIStyle))
                            {
                                GUIStyle s = f.GetValue(__instance) as GUIStyle;
                                if (s != null) { s.font = CustomFontRegular; s.fontStyle = FontStyle.Bold; s.richText = true; }
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

        public static void ActionButton_Prefix(ref string label)
        {
            if (string.IsNullOrEmpty(label)) return;
            string tr;
            if (UiTexts.TryGetValue(label.Trim(), out tr)) label = tr;
        }

        public static void WindowModeButton_Prefix(ref string label)
        {
            if (string.IsNullOrEmpty(label)) return;
            string tr;
            if (UiTexts.TryGetValue(label.Trim(), out tr)) label = tr;
        }

        public static void Volume_Prefix(ref string label)
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
                        if ((ext == ".ttf" || ext == ".otf") && AddFontResourceEx(f, FR_PRIVATE, IntPtr.Zero) > 0)
                        {
                            string fname = Path.GetFileNameWithoutExtension(f);
                            if (!candidates.Contains(fname)) candidates.Add(fname);
                        }
                    }
                }
            }

            foreach (var s in new string[] { "Yu Gothic UI Bold", "Yu Gothic UI", "Yu Gothic", "Meiryo Bold", "Meiryo" })
            {
                if (!candidates.Contains(s)) candidates.Add(s);
            }

            CustomFontRegular = Font.CreateDynamicFontFromOSFont(candidates.ToArray(), 18);
            Log("フォント適用: " + ((CustomFontRegular != null) ? CustomFontRegular.name : "null"));
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

                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (f.FieldType == typeof(GUIStyle))
                    {
                        GUIStyle style = f.GetValue(__instance) as GUIStyle;
                        if (style != null)
                        {
                            style.font = CustomFontRegular;
                            style.fontStyle = FontStyle.Bold;
                            style.richText = true;
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
                        if (ItemDescriptions.TryGetValue(id, out tr) && fDesc != null) fDesc.SetValue(item, tr.Replace("\\n", "\n"));
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
                        if (UpgradeDescriptions.TryGetValue(id, out tr)) fDesc.SetValue(up, tr.Replace("\\n", "\n"));
                    }
                }
            }
            catch { }
        }
    }
}
