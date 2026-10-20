using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
        private static readonly HashSet<int> _initializedInstances = new HashSet<int>();

        // 動的ツールチップおよびフォント候補キャッシュ（起動時YAMLからプリコンパイル）
        public static List<KeyValuePair<Regex, string>> TooltipRegexRules = new List<KeyValuePair<Regex, string>>();
        public static List<KeyValuePair<string, string>> TooltipStringRules = new List<KeyValuePair<string, string>>();
        public static List<string> FontCandidates = new List<string>();

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
                        if (q1 != -1 && q2 > q1)
                        {
                            string langVal = cfg.Substring(q1 + 1, q2 - q1 - 1).Trim();
                            if (Regex.IsMatch(langVal, @"^[a-zA-Z0-9_-]+$")) CurrentLanguage = langVal;
                        }
                    }
                }

                string yamlFile = Path.Combine(localesDir, CurrentLanguage + ".yaml");
                if (!File.Exists(yamlFile)) yamlFile = Path.Combine(localesDir, CurrentLanguage + ".yml");
                if (!File.Exists(yamlFile)) yamlFile = Path.Combine(localesDir, "ja.yaml");
                if (!File.Exists(yamlFile)) yamlFile = Path.Combine(localesDir, "ja.yml");

                if (File.Exists(yamlFile))
                {
                    Log("YAML辞書読込: " + Path.GetFileName(yamlFile));
                    ParseYaml(File.ReadAllText(yamlFile, Encoding.UTF8));
                }

                Log(string.Format("辞書読込完了: アイテム({0}), スキル({1}), ブランチ({2}), UI({3}), 動的ツールチップ(Regex:{4}, String:{5}), フォント候補({6})",
                    ItemNames.Count, SkillNames.Count, BranchNames.Count, UiTexts.Count, TooltipRegexRules.Count, TooltipStringRules.Count, FontCandidates.Count));
            }
            catch (Exception ex) { Log("設定読込エラー: " + ex); }
        }

        public static string ApplyScale(string text, double scale)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (scale > 0.01 && Math.Abs(scale - 1.0) > 0.001)
            {
                int pct = (int)Math.Round(scale * 100.0);
                return string.Format("<size={0}%>{1}</size>", pct, text);
            }
            return text;
        }

        // ゼロアロケーション指向の高速UIテキスト翻訳
        public static string TranslateUi(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            
            // 1. 完全一致ファースト (Trimによるヒープアロケーションなし)
            string tr;
            if (UiTexts.TryGetValue(text, out tr)) return tr;

            // 2. 空白がある場合のみTrimして置換
            string trimmed = text.Trim();
            if (trimmed.Length != text.Length && UiTexts.TryGetValue(trimmed, out tr))
            {
                return text.Replace(trimmed, tr);
            }

            return text;
        }

        public static string TranslateUiOrPattern(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string tr = TranslateUi(text);
            if (!object.ReferenceEquals(tr, text) && tr != text) return tr;
            return TranslateDynamicTooltip(text);
        }

        public static string TranslateDynamicTooltip(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 1. 正規表現ルールの適用（順序保証）
            for (int i = 0; i < TooltipRegexRules.Count; i++)
            {
                var rule = TooltipRegexRules[i];
                text = rule.Key.Replace(text, rule.Value);
            }

            // 2. 部分一致文字列ルールの適用（順序保証）
            for (int i = 0; i < TooltipStringRules.Count; i++)
            {
                var rule = TooltipStringRules[i];
                if (text.IndexOf(rule.Key, StringComparison.Ordinal) != -1)
                {
                    text = text.Replace(rule.Key, rule.Value);
                }
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

            TooltipRegexRules.Clear();
            TooltipStringRules.Clear();
            FontCandidates.Clear();

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
                    string k = null;
                    string val = null;

                    if (trimmed.StartsWith("\""))
                    {
                        int endQ = trimmed.IndexOf('"', 1);
                        if (endQ != -1)
                        {
                            k = trimmed.Substring(1, endQ - 1);
                            int colon = trimmed.IndexOf(':', endQ + 1);
                            val = (colon != -1) ? trimmed.Substring(colon + 1).Trim() : "";
                        }
                    }
                    else if (trimmed.StartsWith("'"))
                    {
                        int endQ = trimmed.IndexOf('\'', 1);
                        if (endQ != -1)
                        {
                            k = trimmed.Substring(1, endQ - 1);
                            int colon = trimmed.IndexOf(':', endQ + 1);
                            val = (colon != -1) ? trimmed.Substring(colon + 1).Trim() : "";
                        }
                    }

                    if (k == null)
                    {
                        int colon = trimmed.IndexOf(':');
                        if (colon != -1)
                        {
                            k = trimmed.Substring(0, colon).Trim().Trim('"', '\'');
                            val = trimmed.Substring(colon + 1).Trim();
                        }
                    }

                    if (k != null)
                    {
                        currentKey = k;

                        if (currentSection == "meta" && k.Equals("fonts", StringComparison.OrdinalIgnoreCase))
                        {
                            string unquoted = Unquote(val);
                            if (!string.IsNullOrEmpty(unquoted))
                            {
                                string[] parts = unquoted.Split(',');
                                foreach (var p in parts)
                                {
                                    string f = p.Trim();
                                    if (!string.IsNullOrEmpty(f) && !FontCandidates.Contains(f)) FontCandidates.Add(f);
                                }
                            }
                            continue;
                        }

                        if (currentSection == "tooltip_regex")
                        {
                            string unquotedVal = Unquote(val);
                            try
                            {
                                Regex r = new Regex(k, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                                TooltipRegexRules.Add(new KeyValuePair<Regex, string>(r, unquotedVal));
                            }
                            catch (Exception ex)
                            {
                                Log("正規表現コンパイルエラー: " + k + " -> " + ex.Message);
                            }
                            continue;
                        }

                        if (currentSection == "tooltip_strings")
                        {
                            string unquotedVal = Unquote(val);
                            TooltipStringRules.Add(new KeyValuePair<string, string>(k, unquotedVal));
                            continue;
                        }

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
                                sectionData[currentSection][currentKey].Text = Unquote(val);
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

            foreach (var kvSec in sectionData)
            {
                string sec = kvSec.Key;
                foreach (var kvEnt in kvSec.Value)
                {
                    string id = kvEnt.Key;
                    ParsedEntry ent = kvEnt.Value;

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

        private static void SetupHooks(Assembly asm)
        {
            if (_assemblyHooked) return;
            _assemblyHooked = true;

            try
            {
                Harmony harmony = new Harmony("com.translator.onemillioncoins");

                // 1. UnityEngine.GUI.Label フック
                MethodInfo mGuiLabel = AccessTools.Method(typeof(GUI), "Label", new Type[] { typeof(Rect), typeof(string), typeof(GUIStyle) });
                if (mGuiLabel != null) harmony.Patch(mGuiLabel, new HarmonyMethod(AccessTools.Method(typeof(Loader), "GUILabel_Prefix")), null);

                MethodInfo mGuiLabel2 = AccessTools.Method(typeof(GUI), "Label", new Type[] { typeof(Rect), typeof(string) });
                if (mGuiLabel2 != null) harmony.Patch(mGuiLabel2, new HarmonyMethod(AccessTools.Method(typeof(Loader), "GUILabel_Prefix")), null);

                // 2. MainMenuUI フック
                Type mainUiType = asm.GetType("GoldDigger.MainMenuUI");
                if (mainUiType != null)
                {
                    MethodInfo mStart = AccessTools.Method(mainUiType, "Start");
                    if (mStart != null) harmony.Patch(mStart, new HarmonyMethod(AccessTools.Method(typeof(Loader), "MainMenuUI_Start_Prefix")), null);

                    MethodInfo mOnGUI = AccessTools.Method(mainUiType, "OnGUI");
                    if (mOnGUI != null) harmony.Patch(mOnGUI, new HarmonyMethod(AccessTools.Method(typeof(Loader), "MainMenuUI_OnGUI_Prefix")), null);

                    MethodInfo mButton = AccessTools.Method(mainUiType, "MenuButton", new Type[] { typeof(Rect), typeof(string), typeof(bool) });
                    if (mButton != null) harmony.Patch(mButton, new HarmonyMethod(AccessTools.Method(typeof(Loader), "GenericLabel_Prefix")), null);
                }

                // 3. VaultUI フック
                Type vaultUiType = asm.GetType("GoldDigger.VaultUI");
                if (vaultUiType != null)
                {
                    MethodInfo mEnsure = AccessTools.Method(vaultUiType, "Ensure");
                    if (mEnsure != null) harmony.Patch(mEnsure, null, new HarmonyMethod(AccessTools.Method(typeof(Loader), "VaultUI_Ensure_Postfix")));

                    MethodInfo mActionBtn = AccessTools.Method(vaultUiType, "ActionButton", new Type[] { typeof(Rect), typeof(string), typeof(GUIStyle), typeof(bool), typeof(bool) });
                    if (mActionBtn != null) harmony.Patch(mActionBtn, new HarmonyMethod(AccessTools.Method(typeof(Loader), "GenericLabel_Prefix")), null);
                }

                // 4. SettingsPanel フック
                Type settingsType = asm.GetType("GoldDigger.SettingsPanel");
                if (settingsType != null)
                {
                    MethodInfo mWinBtn = AccessTools.Method(settingsType, "WindowModeButton");
                    if (mWinBtn != null) harmony.Patch(mWinBtn, new HarmonyMethod(AccessTools.Method(typeof(Loader), "GenericLabel_Prefix")), null);

                    MethodInfo mVol = AccessTools.Method(settingsType, "Volume");
                    if (mVol != null) harmony.Patch(mVol, new HarmonyMethod(AccessTools.Method(typeof(Loader), "GenericLabel_Prefix")), null);
                }

                // 5. ControlsPanel フック
                Type controlsType = asm.GetType("GoldDigger.ControlsPanel");
                if (controlsType != null)
                {
                    MethodInfo mCatTab = AccessTools.Method(controlsType, "CategoryTab");
                    if (mCatTab != null) harmony.Patch(mCatTab, new HarmonyMethod(AccessTools.Method(typeof(Loader), "GenericLabel_Prefix")), null);
                }

                // 6. InputPrompts フック
                Type inputPromptsType = asm.GetType("GoldDigger.InputPrompts");
                if (inputPromptsType != null)
                {
                    MethodInfo mBindingLabel = AccessTools.Method(inputPromptsType, "BindingLabel", new Type[] { typeof(string) });
                    if (mBindingLabel != null) harmony.Patch(mBindingLabel, null, new HarmonyMethod(AccessTools.Method(typeof(Loader), "Generic_Postfix")));

                    MethodInfo mActionLabel = AccessTools.Method(inputPromptsType, "ActionLabel", new Type[] { typeof(Rect), typeof(string), typeof(GUIStyle) });
                    if (mActionLabel != null) harmony.Patch(mActionLabel, new HarmonyMethod(AccessTools.Method(typeof(Loader), "GenericText_Prefix")), null);

                    MethodInfo mGetCaption = AccessTools.Method(inputPromptsType, "GetActionCaption", new Type[] { typeof(string) });
                    if (mGetCaption != null) harmony.Patch(mGetCaption, null, new HarmonyMethod(AccessTools.Method(typeof(Loader), "Generic_Postfix")));

                    MethodInfo mLabel = AccessTools.Method(inputPromptsType, "Label", new Type[] { typeof(string) });
                    if (mLabel != null) harmony.Patch(mLabel, null, new HarmonyMethod(AccessTools.Method(typeof(Loader), "Generic_Postfix")));
                }

                // 7. GoldDiggerHUD フック
                Type hudType = asm.GetType("GoldDigger.GoldDiggerHUD");
                if (hudType != null)
                {
                    MethodInfo mTreeInput = AccessTools.Method(hudType, "DrawTreeInput", new Type[] { typeof(Rect), typeof(string), typeof(string) });
                    if (mTreeInput != null) harmony.Patch(mTreeInput, new HarmonyMethod(AccessTools.Method(typeof(Loader), "GenericLabel_Prefix")), null);
                }

                // 8. GameProgressManager (動的スタッツ) フック
                Type progType = asm.GetType("GoldDigger.GameProgressManager");
                if (progType != null)
                {
                    MethodInfo mClearDesc = AccessTools.Method(progType, "ClearSkillDescription");
                    if (mClearDesc != null) harmony.Patch(mClearDesc, null, new HarmonyMethod(AccessTools.Method(typeof(Loader), "ClearSkillDescription_Postfix")));

                    MethodInfo mSkillDesc = AccessTools.Method(progType, "SkillDescription");
                    if (mSkillDesc != null) harmony.Patch(mSkillDesc, null, new HarmonyMethod(AccessTools.Method(typeof(Loader), "SkillDescription_Postfix")));
                }

                // 9. SkillDefinition.get_MilestoneLabel フック
                Type skillDefType = asm.GetType("GoldDigger.SkillDefinition");
                if (skillDefType != null)
                {
                    MethodInfo mMilestone = AccessTools.Method(skillDefType, "get_MilestoneLabel");
                    if (mMilestone != null) harmony.Patch(mMilestone, null, new HarmonyMethod(AccessTools.Method(typeof(Loader), "MilestoneLabel_Postfix")));
                }

                // 10. ItemCatalog & UpgradeCatalog
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

        // --- 集約された Harmony パッチハンドラ ---

        public static void GUILabel_Prefix(ref string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = TranslateUiOrPattern(text);
        }

        // label 引数を書き換える全メソッドを集約
        public static void GenericLabel_Prefix(ref string label)
        {
            if (string.IsNullOrEmpty(label)) return;
            label = TranslateUi(label);
        }

        // text 引数を書き換える全メソッドを集約
        public static void GenericText_Prefix(ref string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = TranslateUi(text);
        }

        // 戻り値 __result を書き換える全メソッドを集約
        public static void Generic_Postfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            __result = TranslateUi(__result);
        }

        public static void MainMenuUI_Start_Prefix(object __instance)
        {
            if (_engineInitialized) return;
            _engineInitialized = true;
            try { InitializeFonts(); } catch { }
        }

        // 最適化された OnGUI ハンドラ（初期化済みなら最速早期リターン）
        public static void MainMenuUI_OnGUI_Prefix(object __instance)
        {
            if (CustomFontRegular == null || __instance == null) return;
            int hash = __instance.GetHashCode();
            if (_initializedInstances.Contains(hash)) return;
            _initializedInstances.Add(hash);

            try
            {
                if (GUI.skin != null && GUI.skin.font != CustomFontRegular)
                {
                    GUI.skin.font = CustomFontRegular;
                    if (GUI.skin.label != null) GUI.skin.label.richText = true;
                    if (GUI.skin.button != null) GUI.skin.button.richText = true;
                    if (GUI.skin.box != null) GUI.skin.box.richText = true;
                }

                Type t = __instance.GetType();
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (f.FieldType == typeof(GUIStyle))
                    {
                        GUIStyle s = f.GetValue(__instance) as GUIStyle;
                        if (s != null && s.font != CustomFontRegular)
                        {
                            s.font = CustomFontRegular;
                            s.fontStyle = FontStyle.Bold;
                            s.richText = true;
                        }
                    }
                }
            }
            catch { }
        }

        public static void VaultUI_Ensure_Postfix(object __instance)
        {
            if (CustomFontRegular == null || __instance == null) return;
            int hash = __instance.GetHashCode();
            if (_initializedInstances.Contains(hash)) return;
            _initializedInstances.Add(hash);

            try
            {
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
                        if (style != null && style.font != CustomFontRegular)
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

        public static void ClearSkillDescription_Postfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            __result = TranslateDynamicTooltip(__result);
        }

        public static void SkillDescription_Postfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            __result = TranslateDynamicTooltip(__result);
        }

        public static void MilestoneLabel_Postfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            __result = TranslateUi(__result);
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

            if (FontCandidates != null && FontCandidates.Count > 0)
            {
                foreach (var s in FontCandidates)
                {
                    if (!candidates.Contains(s)) candidates.Add(s);
                }
            }
            foreach (var s in new string[] { "Yu Gothic UI Bold", "Microsoft YaHei Bold", "Malgun Gothic Bold", "Segoe UI Bold", "Arial Bold" })
            {
                if (!candidates.Contains(s)) candidates.Add(s);
            }

            CustomFontRegular = Font.CreateDynamicFontFromOSFont(candidates.ToArray(), 18);
            if (CustomFontRegular != null)
            {
                try
                {
                    StringBuilder sbChars = new StringBuilder(4096);
                    for (char c = ' '; c <= '~'; c++) sbChars.Append(c);
                    foreach (var v in UiTexts.Values) if (!string.IsNullOrEmpty(v)) sbChars.Append(v);
                    foreach (var v in SkillNames.Values) if (!string.IsNullOrEmpty(v)) sbChars.Append(v);
                    foreach (var v in ItemNames.Values) if (!string.IsNullOrEmpty(v)) sbChars.Append(v);
                    foreach (var v in BranchNames.Values) if (!string.IsNullOrEmpty(v)) sbChars.Append(v);
                    string warmup = sbChars.ToString();
                    CustomFontRegular.RequestCharactersInTexture(warmup, 18, FontStyle.Bold);
                    CustomFontRegular.RequestCharactersInTexture(warmup, 14, FontStyle.Bold);
                    CustomFontRegular.RequestCharactersInTexture(warmup, 24, FontStyle.Bold);
                    Log(string.Format("フォント適用: {0} (事前ウォームアップ {1} 文字)", CustomFontRegular.name, warmup.Length));
                }
                catch (Exception ex) { Log("フォントウォームアップエラー: " + ex); }
            }
            else
            {
                Log("フォント適用: null");
            }
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
                    Type t = item.GetType();
                    FieldInfo fId = t.GetField("Id");
                    FieldInfo fName = t.GetField("Name");
                    FieldInfo fDesc = t.GetField("Description");

                    if (fId != null)
                    {
                        string id = fId.GetValue(item).ToString();
                        string trName, trDesc;
                        if (ItemNames.TryGetValue(id, out trName) && fName != null) fName.SetValue(item, trName);
                        if (ItemDescriptions.TryGetValue(id, out trDesc) && fDesc != null) fDesc.SetValue(item, trDesc);
                    }
                }
            }
            catch (Exception ex) { Log("ItemCatalog翻訳適用エラー: " + ex); }
        }

        public static void UpgradeCatalog_CreateDefaults_Postfix(object __result)
        {
            try
            {
                if (__result == null) return;
                Array upgs = __result as Array;
                if (upgs == null) return;

                foreach (var upg in upgs)
                {
                    if (upg == null) continue;
                    Type t = upg.GetType();
                    FieldInfo fId = t.GetField("Type");
                    FieldInfo fDesc = t.GetField("Description");

                    if (fId != null && fDesc != null)
                    {
                        string id = fId.GetValue(upg).ToString();
                        string trDesc;
                        if (UpgradeDescriptions.TryGetValue(id, out trDesc)) fDesc.SetValue(upg, trDesc);
                    }
                }
            }
            catch (Exception ex) { Log("UpgradeCatalog翻訳適用エラー: " + ex); }
        }
    }
}
