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
        private static HashSet<int> _initializedInstances = new HashSet<int>();

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

        public static string TranslateUi(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string trimmed = text.Trim();
            string tr;
            if (UiTexts.TryGetValue(trimmed, out tr)) return text.Replace(trimmed, tr);
            return text;
        }

        public static string TranslateUiOrPattern(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string trimmed = text.Trim();

            string tr;
            if (UiTexts.TryGetValue(trimmed, out tr)) return text.Replace(trimmed, tr);

            return TranslateDynamicTooltip(text);
        }

        public static string TranslateDynamicTooltip(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            if (CurrentLanguage == "zh")
            {
                text = text.Replace("Learn prerequisites to see your stat changes.", "学习前置技能以查看属性变化。");
                text = text.Replace("Click node to learn", "点击节点进行学习");
                text = text.Replace("Press submit to learn", "按确认键学习");
                text = text.Replace("NEW MECHANIC", "解锁新机制");
                text = text.Replace("Not learned in this build", "未在当前构筑中学习");
                text = text.Replace("These upgrades are working for you. Explore another route when you are ready.", "这些强化已生效。准备就绪后请探索其他路线。");
                text = text.Replace("Route complete", "路线已完成");
                text = text.Replace("BRANCH ENTRY", "分支入口");
                text = text.Replace("START HERE", "从这里开始");

                text = text.Replace("Hands <size=16>(Tool)</size>", "双手 <size=16>(工具)</size>");
                text = text.Replace("Shovel <size=16>(Tool)</size>", "铁锹 <size=16>(工具)</size>");
                text = text.Replace("Magnet <size=16>(Tool)</size>", "磁铁 <size=16>(工具)</size>");

                text = Regex.Replace(text, @"(\bHands\b)\s*·\s*with your upgrades", "双手 · 生效中的强化", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"(\bShovel\b)\s*·\s*with your upgrades", "铁锹 · 生效中的强化", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"(\bMagnet\b)\s*·\s*with your upgrades", "磁铁 · 生效中的强化", RegexOptions.IgnoreCase);
                text = text.Replace("with your upgrades", "生效中的强化");

                text = text.Replace("Grip coin limit:", "抓取金币上限:");
                text = text.Replace("Grip radius:", "抓取范围:");
                text = text.Replace("Sweet spot starts at:", "甜点触发时机:");
                text = text.Replace("Sweet spot length:", "甜点持续时间:");
                text = text.Replace("Normal pickups resume after:", "恢复正常收集时间:");
                text = text.Replace("Recharge after a miss:", "失误后充能时间:");
                text = text.Replace("Recharge:", "充能时间:");
                text = text.Replace("All tool speed", "全部工具速度");
                text = text.Replace("Hands coins per grab", "双手每次抓取金币数");

                text = Regex.Replace(text, @"(\d+)\s+points?\s*·\s*Requires\s+", "$1 点 · 需要 ");
                text = Regex.Replace(text, @"Requires\s+", "需要 ");
                text = Regex.Replace(text, @"(\d+)\s+points?\s*·\s*Click node to learn", "$1 点 · 点击节点学习");
                text = Regex.Replace(text, @"(\d+)\s+points?\s*·\s*", "$1 点 · ");
                text = Regex.Replace(text, @"(\d+)\s+POINT(S)?", "$1 点");
                text = Regex.Replace(text, @"(\d+)\s+POINTS SPENT", "已花费 $1 点");
            }
            else if (CurrentLanguage == "ko")
            {
                text = text.Replace("Learn prerequisites to see your stat changes.", "속성 변화를 보려면 선행 스킬을 습득하세요.");
                text = text.Replace("Click node to learn", "습득하려면 노드 클릭");
                text = text.Replace("Press submit to learn", "습득하려면 결정 버튼 누르기");
                text = text.Replace("NEW MECHANIC", "새로운 메커니즘");
                text = text.Replace("Not learned in this build", "현재 빌드에서 미습득");
                text = text.Replace("These upgrades are working for you. Explore another route when you are ready.", "습득한 강화가 적용 중입니다. 준비가 되면 다른 경로를 탐색해 보세요.");
                text = text.Replace("Route complete", "경로 완료");
                text = text.Replace("BRANCH ENTRY", "분기 입구");
                text = text.Replace("START HERE", "여기서 시작");

                text = text.Replace("Hands <size=16>(Tool)</size>", "맨손 <size=16>(도구)</size>");
                text = text.Replace("Shovel <size=16>(Tool)</size>", "삽 <size=16>(도구)</size>");
                text = text.Replace("Magnet <size=16>(Tool)</size>", "자석 <size=16>(도구)</size>");

                text = Regex.Replace(text, @"(\bHands\b)\s*·\s*with your upgrades", "맨손 · 적용 중인 강화", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"(\bShovel\b)\s*·\s*with your upgrades", "삽 · 적용 중인 강화", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"(\bMagnet\b)\s*·\s*with your upgrades", "자석 · 적용 중인 강화", RegexOptions.IgnoreCase);
                text = text.Replace("with your upgrades", "적용 중인 강화");

                text = text.Replace("Grip coin limit:", "그립 수집 한도:");
                text = text.Replace("Grip radius:", "그립 반경:");
                text = text.Replace("Sweet spot starts at:", "스위트 스폿 시작:");
                text = text.Replace("Sweet spot length:", "스위트 스폿 지속 시간:");
                text = text.Replace("Normal pickups resume after:", "일반 수집 복귀 시간:");
                text = text.Replace("Recharge after a miss:", "실패 후 재충전:");
                text = text.Replace("Recharge:", "재충전 시간:");
                text = text.Replace("All tool speed", "모든 도구 속도");
                text = text.Replace("Hands coins per grab", "맨손 1회당 수집량");

                text = Regex.Replace(text, @"(\d+)\s+points?\s*·\s*Requires\s+", "$1 포인트 · 필요: ");
                text = Regex.Replace(text, @"Requires\s+", "필요: ");
                text = Regex.Replace(text, @"(\d+)\s+points?\s*·\s*Click node to learn", "$1 포인트 · 클릭하여 습득");
                text = Regex.Replace(text, @"(\d+)\s+points?\s*·\s*", "$1 포인트 · ");
                text = Regex.Replace(text, @"(\d+)\s+POINT(S)?", "$1 포인트");
                text = Regex.Replace(text, @"(\d+)\s+POINTS SPENT", "$1 포인트 사용됨");
            }
            else if (CurrentLanguage == "fr")
            {
                text = text.Replace("Learn prerequisites to see your stat changes.", "Apprenez les prérequis pour voir vos changements de stats.");
                text = text.Replace("Click node to learn", "Cliquez sur le nœud pour apprendre");
                text = text.Replace("Press submit to learn", "Appuyez sur confirmer pour apprendre");
                text = text.Replace("NEW MECHANIC", "NOUVELLE MÉCANIQUE");
                text = text.Replace("Not learned in this build", "Non appris dans ce build");
                text = text.Replace("These upgrades are working for you. Explore another route when you are ready.", "Ces améliorations fonctionnent pour vous. Explorez une autre voie quand vous serez prêt.");
                text = text.Replace("Route complete", "Itinéraire terminé");
                text = text.Replace("BRANCH ENTRY", "ENTRÉE DE BRANCHE");
                text = text.Replace("START HERE", "COMMENCEZ ICI");

                text = text.Replace("Hands <size=16>(Tool)</size>", "Mains <size=16>(Outil)</size>");
                text = text.Replace("Shovel <size=16>(Tool)</size>", "Pelle <size=16>(Outil)</size>");
                text = text.Replace("Magnet <size=16>(Tool)</size>", "Aimant <size=16>(Outil)</size>");

                text = Regex.Replace(text, @"(\bHands\b)\s*·\s*with your upgrades", "Mains · avec vos améliorations", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"(\bShovel\b)\s*·\s*with your upgrades", "Pelle · avec vos améliorations", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"(\bMagnet\b)\s*·\s*with your upgrades", "Aimant · avec vos améliorations", RegexOptions.IgnoreCase);
                text = text.Replace("with your upgrades", "avec vos améliorations");

                text = text.Replace("Grip coin limit:", "Limite de pièces par prise :");
                text = text.Replace("Grip radius:", "Rayon de prise :");
                text = text.Replace("Sweet spot starts at:", "Début de la zone idéale :");
                text = text.Replace("Sweet spot length:", "Durée de la zone idéale :");
                text = text.Replace("Normal pickups resume after:", "Reprise des ramassages normaux après :");
                text = text.Replace("Recharge after a miss:", "Recharge après un échec :");
                text = text.Replace("Recharge:", "Recharge :");
                text = text.Replace("All tool speed", "Vitesse de tous les outils");
                text = text.Replace("Hands coins per grab", "Pièces par prise (mains)");

                text = Regex.Replace(text, @"(\d+)\s+points?\s*·\s*Requires\s+", "$1 points · Requis : ");
                text = Regex.Replace(text, @"Requires\s+", "Requis : ");
                text = Regex.Replace(text, @"(\d+)\s+points?\s*·\s*Click node to learn", "$1 points · Cliquez pour apprendre");
                text = Regex.Replace(text, @"(\d+)\s+points?\s*·\s*", "$1 points · ");
                text = Regex.Replace(text, @"(\d+)\s+POINT(S)?", "$1 POINT(S)");
                text = Regex.Replace(text, @"(\d+)\s+POINTS SPENT", "$1 POINTS DÉPENSÉS");
            }
            else // ja
            {
                text = text.Replace("Learn prerequisites to see your stat changes.", "前提スキルを習得するとステータス変化が表示されます。");
                text = text.Replace("Click node to learn", "クリックして習得");
                text = text.Replace("Press submit to learn", "決定ボタンで習得");
                text = text.Replace("NEW MECHANIC", "新機能解放");
                text = text.Replace("Not learned in this build", "未習得");
                text = text.Replace("These upgrades are working for you. Explore another route when you are ready.", "習得した強化が適用されています。準備ができたら別のルートも探索しましょう。");
                text = text.Replace("Route complete", "ルート完了");
                text = text.Replace("BRANCH ENTRY", "ブランチ入口");
                text = text.Replace("START HERE", "ここから開始");

                text = text.Replace("Hands <size=16>(Tool)</size>", "素手 <size=16>(ツール)</size>");
                text = text.Replace("Shovel <size=16>(Tool)</size>", "シャベル <size=16>(ツール)</size>");
                text = text.Replace("Magnet <size=16>(Tool)</size>", "マグネット <size=16>(ツール)</size>");

                text = Regex.Replace(text, @"(\bHands\b)\s*·\s*with your upgrades", "素手 · 適用中の強化", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"(\bShovel\b)\s*·\s*with your upgrades", "シャベル · 適用中の強化", RegexOptions.IgnoreCase);
                text = Regex.Replace(text, @"(\bMagnet\b)\s*·\s*with your upgrades", "マグネット · 適用中の強化", RegexOptions.IgnoreCase);
                text = text.Replace("with your upgrades", "適用中の強化");

                text = text.Replace("Grip coin limit:", "グリップ回収上限:");
                text = text.Replace("Grip radius:", "グリップ範囲:");
                text = text.Replace("Sweet spot starts at:", "スイートスポット発生:");
                text = text.Replace("Sweet spot length:", "スイートスポット持続:");
                text = text.Replace("Normal pickups resume after:", "通常回収への復帰:");
                text = text.Replace("Recharge after a miss:", "失敗時リチャージ:");
                text = text.Replace("Recharge:", "リチャージ時間:");
                text = text.Replace("All tool speed", "全ツール速度");
                text = text.Replace("Hands coins per grab", "素手回収量");

                text = Regex.Replace(text, @"(\d+)\s+points?\s*·\s*Requires\s+", "$1 ポイント · 要 ");
                text = Regex.Replace(text, @"Requires\s+", "要 ");
                text = Regex.Replace(text, @"(\d+)\s+points?\s*·\s*Click node to learn", "$1 ポイント · クリックして習得");
                text = Regex.Replace(text, @"(\d+)\s+points?\s*·\s*", "$1 ポイント · ");
                text = Regex.Replace(text, @"(\d+)\s+POINT(S)?", "$1 ポイント");
                text = Regex.Replace(text, @"(\d+)\s+POINTS SPENT", "$1 ポイント消費済み");
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
                    if (mButton != null) harmony.Patch(mButton, new HarmonyMethod(AccessTools.Method(typeof(Loader), "MenuButton_Prefix")), null);
                }

                // 3. VaultUI フック
                Type vaultUiType = asm.GetType("GoldDigger.VaultUI");
                if (vaultUiType != null)
                {
                    MethodInfo mEnsure = AccessTools.Method(vaultUiType, "Ensure");
                    if (mEnsure != null) harmony.Patch(mEnsure, null, new HarmonyMethod(AccessTools.Method(typeof(Loader), "VaultUI_Ensure_Postfix")));

                    MethodInfo mActionBtn = AccessTools.Method(vaultUiType, "ActionButton", new Type[] { typeof(Rect), typeof(string), typeof(GUIStyle), typeof(bool), typeof(bool) });
                    if (mActionBtn != null) harmony.Patch(mActionBtn, new HarmonyMethod(AccessTools.Method(typeof(Loader), "ActionButton_Prefix")), null);
                }

                // 4. SettingsPanel フック
                Type settingsType = asm.GetType("GoldDigger.SettingsPanel");
                if (settingsType != null)
                {
                    MethodInfo mWinBtn = AccessTools.Method(settingsType, "WindowModeButton");
                    if (mWinBtn != null) harmony.Patch(mWinBtn, new HarmonyMethod(AccessTools.Method(typeof(Loader), "WindowModeButton_Prefix")), null);

                    MethodInfo mVol = AccessTools.Method(settingsType, "Volume");
                    if (mVol != null) harmony.Patch(mVol, new HarmonyMethod(AccessTools.Method(typeof(Loader), "Volume_Prefix")), null);
                }

                // 5. ControlsPanel フック
                Type controlsType = asm.GetType("GoldDigger.ControlsPanel");
                if (controlsType != null)
                {
                    MethodInfo mCatTab = AccessTools.Method(controlsType, "CategoryTab");
                    if (mCatTab != null) harmony.Patch(mCatTab, new HarmonyMethod(AccessTools.Method(typeof(Loader), "CategoryTab_Prefix")), null);
                }

                // 6. InputPrompts フック
                Type inputPromptsType = asm.GetType("GoldDigger.InputPrompts");
                if (inputPromptsType != null)
                {
                    MethodInfo mBindingLabel = AccessTools.Method(inputPromptsType, "BindingLabel", new Type[] { typeof(string) });
                    if (mBindingLabel != null) harmony.Patch(mBindingLabel, null, new HarmonyMethod(AccessTools.Method(typeof(Loader), "BindingLabel_Postfix")));

                    MethodInfo mActionLabel = AccessTools.Method(inputPromptsType, "ActionLabel", new Type[] { typeof(Rect), typeof(string), typeof(GUIStyle) });
                    if (mActionLabel != null) harmony.Patch(mActionLabel, new HarmonyMethod(AccessTools.Method(typeof(Loader), "ActionLabel_Prefix")), null);

                    MethodInfo mGetCaption = AccessTools.Method(inputPromptsType, "GetActionCaption", new Type[] { typeof(string) });
                    if (mGetCaption != null) harmony.Patch(mGetCaption, null, new HarmonyMethod(AccessTools.Method(typeof(Loader), "GetActionCaption_Postfix")));

                    MethodInfo mLabel = AccessTools.Method(inputPromptsType, "Label", new Type[] { typeof(string) });
                    if (mLabel != null) harmony.Patch(mLabel, null, new HarmonyMethod(AccessTools.Method(typeof(Loader), "InputLabel_Postfix")));
                }

                // 7. GoldDiggerHUD フック
                Type hudType = asm.GetType("GoldDigger.GoldDiggerHUD");
                if (hudType != null)
                {
                    MethodInfo mTreeInput = AccessTools.Method(hudType, "DrawTreeInput", new Type[] { typeof(Rect), typeof(string), typeof(string) });
                    if (mTreeInput != null) harmony.Patch(mTreeInput, new HarmonyMethod(AccessTools.Method(typeof(Loader), "DrawTreeInput_Prefix")), null);
                }

                // 8. GameProgressManager (ツールチップ動的スタッツ) フック
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
                MenuInspector2.Inspect(asm, ModDir);
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

        // --- Harmony Patches ---

        public static void GUILabel_Prefix(ref string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string orig = text;
            text = TranslateUiOrPattern(text);
            if (orig != text && (orig.Contains("Leader") || orig.Contains("Credit") || orig.Contains("Quit") || text.Contains("ゲーム終了")))
                Log("GUILabel: '" + orig + "' -> '" + text + "'");
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
                    if (GUI.skin != null && GUI.skin.font != CustomFontRegular)
                    {
                        GUI.skin.font = CustomFontRegular;
                        if (GUI.skin.label != null) GUI.skin.label.richText = true;
                        if (GUI.skin.button != null) GUI.skin.button.richText = true;
                        if (GUI.skin.box != null) GUI.skin.box.richText = true;
                    }
                    if (__instance != null)
                    {
                        int hash = __instance.GetHashCode();
                        if (!_initializedInstances.Contains(hash))
                        {
                            _initializedInstances.Add(hash);
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
                    }
                }
            }
            catch { }
        }

        public static void MenuButton_Prefix(ref string label)
        {
            if (string.IsNullOrEmpty(label)) return;
            label = TranslateUi(label);
        }

        public static void ActionButton_Prefix(ref string label)
        {
            if (string.IsNullOrEmpty(label)) return;
            string orig = label;
            label = TranslateUi(label);
            if (orig != label) Log("ActionButton: '" + orig + "' -> '" + label + "'");
        }

        public static void WindowModeButton_Prefix(ref string label)
        {
            if (string.IsNullOrEmpty(label)) return;
            label = TranslateUi(label);
        }

        public static void Volume_Prefix(ref string label)
        {
            if (string.IsNullOrEmpty(label)) return;
            label = TranslateUi(label);
        }

        public static void CategoryTab_Prefix(ref string label)
        {
            if (string.IsNullOrEmpty(label)) return;
            label = TranslateUi(label);
        }

        public static void BindingLabel_Postfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            __result = TranslateUi(__result);
        }

        public static void ActionLabel_Prefix(ref string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = TranslateUi(text);
        }

        public static void GetActionCaption_Postfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            __result = TranslateUi(__result);
        }

        public static void InputLabel_Postfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            __result = TranslateUi(__result);
        }

        public static void DrawTreeInput_Prefix(ref string label)
        {
            if (string.IsNullOrEmpty(label)) return;
            label = TranslateUi(label);
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
            if (__result == "NEW MECHANIC")
            {
                if (CurrentLanguage == "zh") __result = "解锁新机制";
                else if (CurrentLanguage == "ko") __result = "새로운 메커니즘";
                else if (CurrentLanguage == "fr") __result = "NOUVELLE MÉCANIQUE";
                else __result = "新機能解放";
            }
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

            if (CurrentLanguage == "zh")
            {
                foreach (var s in new string[] { "Microsoft YaHei Bold", "Microsoft YaHei", "SimHei", "SimSun", "Yu Gothic UI Bold", "Segoe UI Bold" })
                    if (!candidates.Contains(s)) candidates.Add(s);
            }
            else if (CurrentLanguage == "ko")
            {
                foreach (var s in new string[] { "Malgun Gothic Bold", "Malgun Gothic", "Gulim", "Dotum", "Yu Gothic UI Bold", "Segoe UI Bold" })
                    if (!candidates.Contains(s)) candidates.Add(s);
            }
            else if (CurrentLanguage == "fr")
            {
                foreach (var s in new string[] { "Segoe UI Bold", "Segoe UI", "Arial Bold", "Arial", "Yu Gothic UI Bold" })
                    if (!candidates.Contains(s)) candidates.Add(s);
            }
            else // ja
            {
                foreach (var s in new string[] { "Yu Gothic UI Bold", "Yu Gothic UI", "Yu Gothic", "Meiryo Bold", "Meiryo", "Segoe UI Bold" })
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

        public static void VaultUI_Ensure_Postfix(object __instance)
        {
            try
            {
                if (CustomFontRegular == null || __instance == null) return;
                int hash = __instance.GetHashCode();
                if (_initializedInstances.Contains(hash)) return;
                _initializedInstances.Add(hash);

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
