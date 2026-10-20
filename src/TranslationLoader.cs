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

        // コンパイル済み正規表現キャッシュ（パフォーマンス最適化）
        private static readonly Regex RegPointsRequires = new Regex(@"(\d+)\s+points?\s*·\s*Requires\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RegRequires = new Regex(@"Requires\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RegPointsClick = new Regex(@"(\d+)\s+points?\s*·\s*Click node to learn", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RegPointsDot = new Regex(@"(\d+)\s+points?\s*·\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RegPoints = new Regex(@"(\d+)\s+POINT(S)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RegPointsSpent = new Regex(@"(\d+)\s+POINTS SPENT", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RegHandsUpgrades = new Regex(@"(\bHands\b)\s*[^a-zA-Z0-9\s]\s*with your upgrades", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RegShovelUpgrades = new Regex(@"(\bShovel\b)\s*[^a-zA-Z0-9\s]\s*with your upgrades", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RegMagnetUpgrades = new Regex(@"(\bMagnet\b)\s*[^a-zA-Z0-9\s]\s*with your upgrades", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RegToolUpgrades = new Regex(@"(\b[a-zA-Z]+\b)\s*[^a-zA-Z0-9\s]\s*with your upgrades", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RegGeneralUpgrades = new Regex(@"[^a-zA-Z0-9\s]\s*with your upgrades", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

            if (CurrentLanguage == "zh")
            {
                text = RegHandsUpgrades.Replace(text, "双手 · 生效中的强化");
                text = RegShovelUpgrades.Replace(text, "铁锹 · 生效中的强化");
                text = RegMagnetUpgrades.Replace(text, "磁铁 · 生效中的强化");
                text = RegToolUpgrades.Replace(text, "$1 · 生效中的强化");
                text = RegGeneralUpgrades.Replace(text, " · 生效中的强化");

                text = text.Replace("Learn prerequisites to see your stat changes.", "学习前置技能以查看属性变化。")
                           .Replace("Click node to learn", "点击节点进行学习")
                           .Replace("Press submit to learn", "按确认键学习")
                           .Replace("NEW MECHANIC", "解锁新机制")
                           .Replace("MASTERY", "精通")
                           .Replace("IMPROVEMENT", "能力提升")
                           .Replace("Not learned in this build", "未在当前构筑中学习")
                           .Replace("These upgrades are working for you. Explore another route when you are ready.", "这些强化已生效。准备就绪后请探索其他路线。")
                           .Replace("Route complete", "路线已完成")
                           .Replace("BRANCH ENTRY", "分支入口")
                           .Replace("START HERE", "从这里开始")
                           .Replace("Hands <size=16>(Tool)</size>", "双手 <size=16>(工具)</size>")
                           .Replace("Shovel <size=16>(Tool)</size>", "铁锹 <size=16>(工具)</size>")
                           .Replace("Magnet <size=16>(Tool)</size>", "磁铁 <size=16>(工具)</size>")
                           .Replace("With your upgrades", "生效中的强化")
                           .Replace(" • with your upgrades", " • 生效中的强化")
                           .Replace("with your upgrades", "生效中的强化")
                           // 铁锹
                           .Replace("Scoop coin limit:", "强力收集金币上限:")
                           .Replace("Scoop radius:", "强力收集范围:")
                           .Replace("Normal scoops resume after 1s", "1秒后恢复普通收集")
                           .Replace("Normal scoops resume after:", "恢复普通收集时间:")
                           .Replace("Slam coin limit:", "猛砸金币上限:")
                           .Replace("Slam radius:", "猛砸范围:")
                           .Replace("Digging depth:", "挖掘深度:")
                           .Replace("Leap range: 25 m", "跳跃距离: 25米")
                           .Replace("Coins per scoop:", "每次铁锹收集金币数:")
                           .Replace("Time between scoops:", "铁锹收集间隔:")
                           // 磁铁/闪电/重击
                           .Replace("Coins per bounce (shared):", "每次弹跳金币 (共享):")
                           .Replace("Coins per bounce:", "每次弹跳金币:")
                           .Replace("Strike coin limit:", "重击金币上限:")
                           .Replace("Collection radius:", "收集范围:")
                           .Replace("Chance per ready pickup:", "每次就绪收集的触发几率:")
                           .Replace("Guaranteed within:", "保底次数:")
                           .Replace(" ready pickups", "次就绪收集")
                           .Replace("Starting bounces:", "初始弹跳次数:")
                           .Replace("Chance of another bounce:", "额外弹跳几率:")
                           .Replace("Jump distance:", "弹跳距离:")
                           .Replace("Chains:", "连锁数:")
                           // 淘金热/便携银行/投递
                           .Replace("Gold Rush duration:", "淘金热持续时间:")
                           .Replace("Automatic collection rate:", "自动收集速率:")
                           .Replace("Automatic rate cap:", "自动速率上限:")
                           .Replace("Manual unload speed:", "手动卸载速度:")
                           .Replace("% of station", "% (相比投递站)")
                           .Replace("Minimum manual deposit:", "最低手动投递量:")
                           .Replace("During Cash flow:", "现金流期间:")
                           .Replace(" coins/s", " 金币/秒")
                           // 双手/幽灵/抓取
                           .Replace("Coins per ghost grab:", "幽灵每次抓取金币数:")
                           .Replace("Time between grabs:", "抓取间隔:")
                           .Replace("Full Flow: 6 hands, one grab every 0.1s", "满额流动: 6只手，每0.1秒抓取一次")
                           .Replace("Hands radius (grip excluded):", "双手范围 (不含抓取):")
                           .Replace("Shovel/Magnet helpers: 30% fewer coins", "铁锹/磁铁助手: 金币减少30%")
                           .Replace("Coin limit at max levels:", "最高等级金币上限:")
                           .Replace("Radius at max levels:", "最高等级范围:")
                           .Replace("Maximum levels:", "最高等级:")
                           .Replace("Per level: +20% base coins, +5% base radius", "每级: 基础金币+20%，基础范围+5%")
                           .Replace("Sweet spot: 0.01s shorter per level", "每级甜点时间缩短0.01秒")
                           .Replace("Sweet spot at level 0:", "0级时的甜点持续时间:")
                           .Replace("Grip coin limit:", "抓取金币上限:")
                           .Replace("Grip radius:", "抓取范围:")
                           .Replace("Sweet spot starts at:", "甜点触发时机:")
                           .Replace("Sweet spot length:", "甜点持续时间:")
                           .Replace("Normal pickups resume after:", "恢复正常收集时间:")
                           .Replace("Recharge after a miss:", "失误后充能时间:")
                           .Replace("Recharge:", "充能时间:")
                           .Replace("Cooldown: 40s", "冷却时间: 40秒")
                           .Replace("Cooldown: 4s", "冷却时间: 4秒")
                           .Replace("Cooldown: 3s", "冷却时间: 3秒")
                           .Replace("Cooldown:", "冷却时间:")
                           // 被动强化属性
                           .Replace("All tool coins per pickup", "全部工具每次收集金币数")
                           .Replace("All tool pickup radius", "全部工具收集范围")
                           .Replace("All tool speed", "全部工具速度")
                           .Replace("Hands coins per grab", "双手每次抓取金币数")
                           .Replace("Heavy Strike coin limit", "重击金币上限")
                           .Replace("Lightning coin limit", "闪电金币上限")
                           .Replace("Lightning jump distance", "闪电弹跳距离")
                           .Replace("Tool reach +", "工具攻击范围 +")
                           .Replace("Tool reach", "工具攻击范围")
                           .Replace("% of base", "% (基础值)")
                           .Replace("Deposit speed", "投递速度")
                           .Replace("Bag capacity", "背包容量");

                text = RegPointsRequires.Replace(text, "$1 点 · 需要 ");
                text = RegRequires.Replace(text, "需要 ");
                text = RegPointsClick.Replace(text, "$1 点 · 点击节点学习");
                text = RegPointsDot.Replace(text, "$1 点 · ");
                text = RegPoints.Replace(text, "$1 点");
                text = RegPointsSpent.Replace(text, "已花费 $1 点");
            }
            else if (CurrentLanguage == "ko")
            {
                text = RegHandsUpgrades.Replace(text, "맨손 · 적용 중인 강화");
                text = RegShovelUpgrades.Replace(text, "삽 · 적용 중인 강화");
                text = RegMagnetUpgrades.Replace(text, "자석 · 적용 중인 강화");
                text = RegToolUpgrades.Replace(text, "$1 · 적용 중인 강화");
                text = RegGeneralUpgrades.Replace(text, " · 적용 중인 강화");

                text = text.Replace("Learn prerequisites to see your stat changes.", "속성 변화를 보려면 선행 스킬을 습득하세요.")
                           .Replace("Click node to learn", "습득하려면 노드 클릭")
                           .Replace("Press submit to learn", "습득하려면 결정 버튼 누르기")
                           .Replace("NEW MECHANIC", "새로운 메커니즘")
                           .Replace("MASTERY", "마스터리")
                           .Replace("IMPROVEMENT", "능력 강화")
                           .Replace("Not learned in this build", "현재 빌드에서 미습득")
                           .Replace("These upgrades are working for you. Explore another route when you are ready.", "습득한 강화가 적용 중입니다. 준비가 되면 다른 경로를 탐색해 보세요.")
                           .Replace("Route complete", "경로 완료")
                           .Replace("BRANCH ENTRY", "분기 입구")
                           .Replace("START HERE", "여기서 시작")
                           .Replace("Hands <size=16>(Tool)</size>", "맨손 <size=16>(도구)</size>")
                           .Replace("Shovel <size=16>(Tool)</size>", "삽 <size=16>(도구)</size>")
                           .Replace("Magnet <size=16>(Tool)</size>", "자석 <size=16>(도구)</size>")
                           .Replace("With your upgrades", "적용 중인 강화")
                           .Replace(" • with your upgrades", " • 적용 중인 강화")
                           .Replace("with your upgrades", "적용 중인 강화")
                           // 삽
                           .Replace("Scoop coin limit:", "삽 수집 한도:")
                           .Replace("Scoop radius:", "삽 수집 반경:")
                           .Replace("Normal scoops resume after 1s", "1초 후 일반 삽 수집 복귀")
                           .Replace("Normal scoops resume after:", "일반 삽 수집 복귀 시간:")
                           .Replace("Slam coin limit:", "슬램 수집 한도:")
                           .Replace("Slam radius:", "슬램 반경:")
                           .Replace("Digging depth:", "굴착 깊이:")
                           .Replace("Leap range: 25 m", "도약 거리: 25m")
                           .Replace("Coins per scoop:", "1회당 삽 수집량:")
                           .Replace("Time between scoops:", "삽 수집 간격:")
                           // 자석/번개/강타
                           .Replace("Coins per bounce (shared):", "바운스당 코인 (공유):")
                           .Replace("Coins per bounce:", "바운스당 코인:")
                           .Replace("Strike coin limit:", "강타 수집 한도:")
                           .Replace("Collection radius:", "수집 반경:")
                           .Replace("Chance per ready pickup:", "준비된 수집당 발동 확률:")
                           .Replace("Guaranteed within:", "확정 발동까지:")
                           .Replace(" ready pickups", "회 준비된 수집")
                           .Replace("Starting bounces:", "시작 바운스 횟수:")
                           .Replace("Chance of another bounce:", "추가 바운스 확률:")
                           .Replace("Jump distance:", "점프 거리:")
                           .Replace("Chains:", "연쇄 수:")
                           // 골드 러시/포켓 뱅크/투입
                           .Replace("Gold Rush duration:", "골드 러시 지속 시간:")
                           .Replace("Automatic collection rate:", "자동 수집 속도:")
                           .Replace("Automatic rate cap:", "자동 속도 상한:")
                           .Replace("Manual unload speed:", "수동 하역 속도:")
                           .Replace("% of station", "% (투입소 대비)")
                           .Replace("Minimum manual deposit:", "최소 수동 투입량:")
                           .Replace("During Cash flow:", "캐시 플로우 중:")
                           .Replace(" coins/s", " 코인/초")
                           // 맨손/고스트/그립
                           .Replace("Coins per ghost grab:", "고스트 1회당 수집량:")
                           .Replace("Time between grabs:", "수집 간격:")
                           .Replace("Full Flow: 6 hands, one grab every 0.1s", "최대 Flow: 손 6개, 0.1초마다 1회 수집")
                           .Replace("Hands radius (grip excluded):", "맨손 반경 (그립 제외):")
                           .Replace("Shovel/Magnet helpers: 30% fewer coins", "삽/자석 조수: 코인 30% 감소")
                           .Replace("Coin limit at max levels:", "최대 레벨 시 수집 한도:")
                           .Replace("Radius at max levels:", "최대 레벨 시 반경:")
                           .Replace("Maximum levels:", "최대 레벨:")
                           .Replace("Per level: +20% base coins, +5% base radius", "레벨당: 기본 코인 +20%, 기본 반경 +5%")
                           .Replace("Sweet spot: 0.01s shorter per level", "레벨당 스위트 스폿 -0.01초")
                           .Replace("Sweet spot at level 0:", "레벨 0 스위트 스폿:")
                           .Replace("Grip coin limit:", "그립 수집 한도:")
                           .Replace("Grip radius:", "그립 반경:")
                           .Replace("Sweet spot starts at:", "스위트 스폿 시작:")
                           .Replace("Sweet spot length:", "스위트 스폿 지속 시간:")
                           .Replace("Normal pickups resume after:", "일반 수집 복귀 시간:")
                           .Replace("Recharge after a miss:", "실패 후 재충전:")
                           .Replace("Recharge:", "재충전 시간:")
                           .Replace("Cooldown: 40s", "재사용 대기시간: 40초")
                           .Replace("Cooldown: 4s", "재사용 대기시간: 4초")
                           .Replace("Cooldown: 3s", "재사용 대기시간: 3초")
                           .Replace("Cooldown:", "재사용 대기시간:")
                           // 패시브 속성
                           .Replace("All tool coins per pickup", "모든 도구 1회당 수집량")
                           .Replace("All tool pickup radius", "모든 도구 수집 반경")
                           .Replace("All tool speed", "모든 도구 속도")
                           .Replace("Hands coins per grab", "맨손 1회당 수집량")
                           .Replace("Heavy Strike coin limit", "강타 수집 한도")
                           .Replace("Lightning coin limit", "번개 수집 한도")
                           .Replace("Lightning jump distance", "번개 점프 거리")
                           .Replace("Tool reach +", "도구 사거리 +")
                           .Replace("Tool reach", "도구 사거리")
                           .Replace("% of base", "% (기본값 기준)")
                           .Replace("Deposit speed", "투입 속도")
                           .Replace("Bag capacity", "가방 용량");

                text = RegPointsRequires.Replace(text, "$1 포인트 · 필요: ");
                text = RegRequires.Replace(text, "필요: ");
                text = RegPointsClick.Replace(text, "$1 포인트 · 클릭하여 습득");
                text = RegPointsDot.Replace(text, "$1 포인트 · ");
                text = RegPoints.Replace(text, "$1 포인트");
                text = RegPointsSpent.Replace(text, "$1 포인트 사용됨");
            }
            else if (CurrentLanguage == "fr")
            {
                text = RegHandsUpgrades.Replace(text, "Mains · avec vos améliorations");
                text = RegShovelUpgrades.Replace(text, "Pelle · avec vos améliorations");
                text = RegMagnetUpgrades.Replace(text, "Aimant · avec vos améliorations");
                text = RegToolUpgrades.Replace(text, "$1 · avec vos améliorations");
                text = RegGeneralUpgrades.Replace(text, " · avec vos améliorations");

                text = text.Replace("Learn prerequisites to see your stat changes.", "Apprenez les prérequis pour voir vos changements de stats.")
                           .Replace("Click node to learn", "Cliquez sur le nœud pour apprendre")
                           .Replace("Press submit to learn", "Appuyez sur confirmer pour apprendre")
                           .Replace("NEW MECHANIC", "NOUVELLE MÉCANIQUE")
                           .Replace("MASTERY", "MAÎTRISE")
                           .Replace("IMPROVEMENT", "AMÉLIORATION")
                           .Replace("Not learned in this build", "Non appris dans ce build")
                           .Replace("These upgrades are working for you. Explore another route when you are ready.", "Ces améliorations fonctionnent pour vous. Explorez une autre voie quand vous serez prêt.")
                           .Replace("Route complete", "Itinéraire terminé")
                           .Replace("BRANCH ENTRY", "ENTRÉE DE BRANCHE")
                           .Replace("START HERE", "COMMENCEZ ICI")
                           .Replace("Hands <size=16>(Tool)</size>", "Mains <size=16>(Outil)</size>")
                           .Replace("Shovel <size=16>(Tool)</size>", "Pelle <size=16>(Outil)</size>")
                           .Replace("Magnet <size=16>(Tool)</size>", "Aimant <size=16>(Outil)</size>")
                           .Replace("With your upgrades", "avec vos améliorations")
                           .Replace(" • with your upgrades", " • avec vos améliorations")
                           .Replace("with your upgrades", "avec vos améliorations")
                           // Pelle
                           .Replace("Scoop coin limit:", "Limite de pièces par coup de pelle :")
                           .Replace("Scoop radius:", "Rayon de coup de pelle :")
                           .Replace("Normal scoops resume after 1s", "Reprise des coups normaux après 1s")
                           .Replace("Normal scoops resume after:", "Reprise des coups normaux après :")
                           .Replace("Slam coin limit:", "Limite de pièces du slam :")
                           .Replace("Slam radius:", "Rayon du slam :")
                           .Replace("Digging depth:", "Profondeur de creusement :")
                           .Replace("Leap range: 25 m", "Portée du saut : 25 m")
                           .Replace("Coins per scoop:", "Pièces par coup de pelle :")
                           .Replace("Time between scoops:", "Intervalle entre les coups :")
                           // Aimant/Éclair/Frappe
                           .Replace("Coins per bounce (shared):", "Pièces par rebond (partagé) :")
                           .Replace("Coins per bounce:", "Pièces par rebond :")
                           .Replace("Strike coin limit:", "Limite de pièces de frappe :")
                           .Replace("Collection radius:", "Rayon de collecte :")
                           .Replace("Chance per ready pickup:", "Chance par ramassage prêt :")
                           .Replace("Guaranteed within:", "Garanti dans les :")
                           .Replace(" ready pickups", " ramassages prêts")
                           .Replace("Starting bounces:", "Rebonds initiaux :")
                           .Replace("Chance of another bounce:", "Chance d'un autre rebond :")
                           .Replace("Jump distance:", "Distance de saut :")
                           .Replace("Chains:", "Chaînes :")
                           // Ruée vers l'or/Banque/Dépôt
                           .Replace("Gold Rush duration:", "Durée de la Ruée vers l'or :")
                           .Replace("Automatic collection rate:", "Taux de collecte automatique :")
                           .Replace("Automatic rate cap:", "Limite de taux automatique :")
                           .Replace("Manual unload speed:", "Vitesse de déchargement manuel :")
                           .Replace("% of station", "% de la station")
                           .Replace("Minimum manual deposit:", "Dépôt manuel minimum :")
                           .Replace("During Cash flow:", "Pendant le flux de trésorerie :")
                           .Replace(" coins/s", " pièces/s")
                           // Mains/Fantôme/Prise
                           .Replace("Coins per ghost grab:", "Pièces par prise de fantôme :")
                           .Replace("Time between grabs:", "Intervalle entre les prises :")
                           .Replace("Full Flow: 6 hands, one grab every 0.1s", "Flow max : 6 mains, une prise toutes les 0.1s")
                           .Replace("Hands radius (grip excluded):", "Rayon des mains (hors prise) :")
                           .Replace("Shovel/Magnet helpers: 30% fewer coins", "Aides Pelle/Aimant : 30% de pièces en moins")
                           .Replace("Coin limit at max levels:", "Limite de pièces au niveau max :")
                           .Replace("Radius at max levels:", "Rayon au niveau max :")
                           .Replace("Maximum levels:", "Niveaux maximum :")
                           .Replace("Per level: +20% base coins, +5% base radius", "Par niveau : +20% pièces de base, +5% rayon de base")
                           .Replace("Sweet spot: 0.01s shorter per level", "Zone idéale : 0.01s de moins par niveau")
                           .Replace("Sweet spot at level 0:", "Zone idéale au niveau 0 :")
                           .Replace("Grip coin limit:", "Limite de pièces par prise :")
                           .Replace("Grip radius:", "Rayon de prise :")
                           .Replace("Sweet spot starts at:", "Début de la zone idéale :")
                           .Replace("Sweet spot length:", "Durée de la zone idéale :")
                           .Replace("Normal pickups resume after:", "Reprise des ramassages normaux après :")
                           .Replace("Recharge after a miss:", "Recharge après un échec :")
                           .Replace("Recharge:", "Recharge :")
                           .Replace("Cooldown: 40s", "Temps de recharge : 40s")
                           .Replace("Cooldown: 4s", "Temps de recharge : 4s")
                           .Replace("Cooldown: 3s", "Temps de recharge : 3s")
                           .Replace("Cooldown:", "Temps de recharge :")
                           // Propriétés passives
                           .Replace("All tool coins per pickup", "Pièces par ramassage (tous outils)")
                           .Replace("All tool pickup radius", "Rayon de ramassage (tous outils)")
                           .Replace("All tool speed", "Vitesse de tous les outils")
                           .Replace("Hands coins per grab", "Pièces par prise (mains)")
                           .Replace("Heavy Strike coin limit", "Limite de pièces de frappe lourde")
                           .Replace("Lightning coin limit", "Limite de pièces d'éclair")
                           .Replace("Lightning jump distance", "Distance de saut d'éclair")
                           .Replace("Tool reach +", "Portée des outils +")
                           .Replace("Tool reach", "Portée des outils")
                           .Replace("% of base", "% de base")
                           .Replace("Deposit speed", "Vitesse de dépôt")
                           .Replace("Bag capacity", "Capacité du sac");

                text = RegPointsRequires.Replace(text, "$1 points · Requis : ");
                text = RegRequires.Replace(text, "Requis : ");
                text = RegPointsClick.Replace(text, "$1 points · Cliquez pour apprendre");
                text = RegPointsDot.Replace(text, "$1 points · ");
                text = RegPoints.Replace(text, "$1 POINT(S)");
                text = RegPointsSpent.Replace(text, "$1 POINTS DÉPENSÉS");
            }
            else // ja
            {
                text = RegHandsUpgrades.Replace(text, "素手 · 適用中の強化");
                text = RegShovelUpgrades.Replace(text, "シャベル · 適用中の強化");
                text = RegMagnetUpgrades.Replace(text, "マグネット · 適用中の強化");
                text = RegToolUpgrades.Replace(text, "$1 · 適用中の強化");
                text = RegGeneralUpgrades.Replace(text, " · 適用中の強化");

                text = text.Replace("Learn prerequisites to see your stat changes.", "前提スキルを習得するとステータス変化が表示されます。")
                           .Replace("Click node to learn", "クリックして習得")
                           .Replace("Press submit to learn", "決定ボタンで習得")
                           .Replace("NEW MECHANIC", "新機能解放")
                           .Replace("MASTERY", "マスタリー")
                           .Replace("IMPROVEMENT", "能力強化")
                           .Replace("Not learned in this build", "未習得")
                           .Replace("These upgrades are working for you. Explore another route when you are ready.", "習得した強化が適用されています。準備ができたら別のルートも探索しましょう。")
                           .Replace("Route complete", "ルート完了")
                           .Replace("BRANCH ENTRY", "ブランチ入口")
                           .Replace("START HERE", "ここから開始")
                           .Replace("Hands <size=16>(Tool)</size>", "素手 <size=16>(ツール)</size>")
                           .Replace("Shovel <size=16>(Tool)</size>", "シャベル <size=16>(ツール)</size>")
                           .Replace("Magnet <size=16>(Tool)</size>", "マグネット <size=16>(ツール)</size>")
                           .Replace("With your upgrades", "適用中の強化")
                           .Replace(" • with your upgrades", " • 適用中の強化")
                           .Replace("with your upgrades", "適用中の強化")
                           // シャベル
                           .Replace("Scoop coin limit:", "すくい取り回収上限:")
                           .Replace("Scoop radius:", "すくい取り範囲:")
                           .Replace("Normal scoops resume after 1s", "通常すくい取りへの復帰: 1秒後")
                           .Replace("Normal scoops resume after:", "通常すくい取りへの復帰:")
                           .Replace("Slam coin limit:", "スラム回収上限:")
                           .Replace("Slam radius:", "スラム範囲:")
                           .Replace("Digging depth:", "掘削深度:")
                           .Replace("Leap range: 25 m", "跳躍距離: 25m")
                           .Replace("Coins per scoop:", "すくい取り回収量:")
                           .Replace("Time between scoops:", "すくい取り間隔:")
                           // マグネット/稲妻/重撃
                           .Replace("Coins per bounce (shared):", "バウンスごとのコイン上限 (共有):")
                           .Replace("Coins per bounce:", "バウンスごとのコイン上限:")
                           .Replace("Strike coin limit:", "ストライク回収上限:")
                           .Replace("Collection radius:", "回収範囲:")
                           .Replace("Chance per ready pickup:", "準備完了時の発動率:")
                           .Replace("Guaranteed within:", "確定発動まで:")
                           .Replace(" ready pickups", "回の準備完了回収")
                           .Replace("Starting bounces:", "初期バウンス回数:")
                           .Replace("Chance of another bounce:", "追加バウンス確率:")
                           .Replace("Jump distance:", "ジャンプ距離:")
                           .Replace("Chains:", "連鎖数:")
                           // ゴールドラッシュ/ポケットバンク/納入
                           .Replace("Gold Rush duration:", "ゴールドラッシュ持続時間:")
                           .Replace("Automatic collection rate:", "自動換金レート:")
                           .Replace("Automatic rate cap:", "自動換金レート上限:")
                           .Replace("Manual unload speed:", "手動搬入速度:")
                           .Replace("% of station", "% (納入所比)")
                           .Replace("Minimum manual deposit:", "手動納入の最小必要量:")
                           .Replace("During Cash flow:", "キャッシュフロー中:")
                           .Replace(" coins/s", " コイン/秒")
                           // 素手/ゴースト/グリップ
                           .Replace("Coins per ghost grab:", "ゴースト回収量:")
                           .Replace("Time between grabs:", "回収間隔:")
                           .Replace("Full Flow: 6 hands, one grab every 0.1s", "フルFlow: 手6本、0.1秒ごとに1回回収")
                           .Replace("Hands radius (grip excluded):", "素手範囲 (グリップ除外):")
                           .Replace("Shovel/Magnet helpers: 30% fewer coins", "シャベル/マグネット時の助手: コイン量-30%")
                           .Replace("Coin limit at max levels:", "最大レベル時の回収上限:")
                           .Replace("Radius at max levels:", "最大レベル時の範囲:")
                           .Replace("Maximum levels:", "最大レベル:")
                           .Replace("Per level: +20% base coins, +5% base radius", "レベルごと: 基本コイン+20%、基本範囲+5%")
                           .Replace("Sweet spot: 0.01s shorter per level", "レベルごとにスイートスポット-0.01秒")
                           .Replace("Sweet spot at level 0:", "レベル0時のスイートスポット:")
                           .Replace("Grip coin limit:", "グリップ回収上限:")
                           .Replace("Grip radius:", "グリップ範囲:")
                           .Replace("Sweet spot starts at:", "スイートスポット発生:")
                           .Replace("Sweet spot length:", "スイートスポット持続:")
                           .Replace("Normal pickups resume after:", "通常回収への復帰:")
                           .Replace("Recharge after a miss:", "失敗時リチャージ:")
                           .Replace("Recharge:", "リチャージ時間:")
                           .Replace("Cooldown: 40s", "クールダウン: 40秒")
                           .Replace("Cooldown: 4s", "クールダウン: 4秒")
                           .Replace("Cooldown: 3s", "クールダウン: 3秒")
                           .Replace("Cooldown:", "クールダウン:")
                           // パッシブ効果項目
                           .Replace("All tool coins per pickup", "全ツールの回収ごとコイン数")
                           .Replace("All tool pickup radius", "全ツールの回収範囲")
                           .Replace("All tool speed", "全ツール速度")
                           .Replace("Hands coins per grab", "素手回収量")
                           .Replace("Heavy Strike coin limit", "ヘビーストライク回収上限")
                           .Replace("Lightning coin limit", "稲妻回収上限")
                           .Replace("Lightning jump distance", "稲妻跳躍距離")
                           .Replace("Tool reach +", "ツールリーチ +")
                           .Replace("Tool reach", "ツールリーチ")
                           .Replace("% of base", "% (基本値比)")
                           .Replace("Deposit speed", "納入速度")
                           .Replace("Bag capacity", "バッグ容量");

                text = RegPointsRequires.Replace(text, "$1 ポイント · 要 ");
                text = RegRequires.Replace(text, "要 ");
                text = RegPointsClick.Replace(text, "$1 ポイント · クリックして習得");
                text = RegPointsDot.Replace(text, "$1 ポイント · ");
                text = RegPoints.Replace(text, "$1 ポイント");
                text = RegPointsSpent.Replace(text, "$1 ポイント消費済み");
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
            if (__result == "NEW MECHANIC")
            {
                if (CurrentLanguage == "zh") __result = "解锁新机制";
                else if (CurrentLanguage == "ko") __result = "새로운 메커니즘";
                else if (CurrentLanguage == "fr") __result = "NOUVELLE MÉCANIQUE";
                else __result = "新機能解放";
            }
            else if (__result == "MASTERY")
            {
                if (CurrentLanguage == "zh") __result = "精通";
                else if (CurrentLanguage == "ko") __result = "마스터리";
                else if (CurrentLanguage == "fr") __result = "MAÎTRISE";
                else __result = "マスタリー";
            }
            else if (__result == "IMPROVEMENT")
            {
                if (CurrentLanguage == "zh") __result = "能力提升";
                else if (CurrentLanguage == "ko") __result = "능력 강화";
                else if (CurrentLanguage == "fr") __result = "AMÉLIORATION";
                else __result = "能力強化";
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
