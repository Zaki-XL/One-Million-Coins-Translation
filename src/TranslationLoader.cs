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
        public static Dictionary<string, string> UiTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static Font CustomFontRegular;
        public static Font CustomFontBold;
        public static Font CustomFontDisplay;

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

            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "Assembly-CSharp")
                {
                    SetupHooks(asm);
                    break;
                }
            }

            Log("初期化完了: Assembly-CSharp のロード待機を開始しました。");
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            if (args.LoadedAssembly.GetName().Name == "Assembly-CSharp")
            {
                SetupHooks(args.LoadedAssembly);
            }
        }

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            string assemblyName = new AssemblyName(args.Name).Name;
            string candidate = Path.Combine(ModDir, assemblyName + ".dll");
            if (File.Exists(candidate))
            {
                return Assembly.LoadFrom(candidate);
            }
            return null;
        }

        public static void Log(string message)
        {
            try
            {
                string line = string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, message);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch { }
        }

        private static void LoadConfigurations()
        {
            try
            {
                string localesDir = Path.Combine(ModDir, "locales");
                string configPath = Path.Combine(localesDir, "config.json");

                if (File.Exists(configPath))
                {
                    string cfgJson = File.ReadAllText(configPath);
                    if (cfgJson.Contains("\"language\""))
                    {
                        int idx = cfgJson.IndexOf("\"language\"");
                        int colon = cfgJson.IndexOf(':', idx);
                        int q1 = cfgJson.IndexOf('"', colon);
                        int q2 = cfgJson.IndexOf('"', q1 + 1);
                        if (q1 != -1 && q2 != -1)
                        {
                            CurrentLanguage = cfgJson.Substring(q1 + 1, q2 - q1 - 1).Trim();
                        }
                    }
                }

                Log("選択言語: " + CurrentLanguage);

                string langFile = Path.Combine(localesDir, CurrentLanguage + ".json");
                if (!File.Exists(langFile))
                {
                    langFile = Path.Combine(localesDir, "ja.json");
                }

                if (File.Exists(langFile))
                {
                    ParseDictionary(File.ReadAllText(langFile));
                    Log(string.Format("辞書読み込み: アイテム({0}), アップグレード({1}), UI({2})",
                        ItemNames.Count, UpgradeDescriptions.Count, UiTexts.Count));
                }
            }
            catch (Exception ex)
            {
                Log("設定読み込みエラー: " + ex);
            }
        }

        private static void ParseDictionary(string json)
        {
            ParseSection(json, "items", (id, name, desc) => {
                if (!string.IsNullOrEmpty(name)) ItemNames[id] = name;
                if (!string.IsNullOrEmpty(desc)) ItemDescriptions[id] = desc;
            });

            ParseSection(json, "upgrades", (id, name, desc) => {
                if (!string.IsNullOrEmpty(desc)) UpgradeDescriptions[id] = desc;
            });

            ParseSection(json, "ui", (id, val, dummy) => {
                if (!string.IsNullOrEmpty(val)) UiTexts[id] = val;
            });
        }

        private static void ParseSection(string json, string sectionName, Action<string, string, string> onEntry)
        {
            int sIdx = json.IndexOf("\"" + sectionName + "\"");
            if (sIdx == -1) return;
            int bStart = json.IndexOf('{', sIdx);
            if (bStart == -1) return;

            string sub = json.Substring(bStart);
            string[] lines = sub.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string curKey = null;
            string curName = null;
            string curDesc = null;

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.StartsWith("\"") && line.EndsWith("{"))
                {
                    if (curKey != null) onEntry(curKey, curName, curDesc);
                    curKey = line.Split('"')[1];
                    curName = null;
                    curDesc = null;
                }
                else if (sectionName == "ui" && line.StartsWith("\"") && line.Contains(":"))
                {
                    string[] parts = line.Split(new char[] { ':' }, 2);
                    if (parts.Length == 2)
                    {
                        string k = parts[0].Trim().Trim('"');
                        string v = ExtractVal(line);
                        if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                        {
                            onEntry(k, v, null);
                        }
                    }
                }
                else if (line.Contains("\"name\"") && line.Contains(":"))
                {
                    curName = ExtractVal(line);
                }
                else if (line.Contains("\"description\"") && line.Contains(":"))
                {
                    curDesc = ExtractVal(line);
                }
                else if (line == "}," || line == "}")
                {
                    if (curKey != null)
                    {
                        onEntry(curKey, curName, curDesc);
                        curKey = null;
                    }
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
                return line.Substring(q1 + 1, q2 - q1 - 1).Replace("\\\"", "\"").Replace("\\\\", "\\");
            }
            return null;
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
                    if (mStart != null)
                    {
                        MethodInfo prefix = AccessTools.Method(typeof(Loader), "MainMenuUI_Start_Prefix");
                        harmony.Patch(mStart, new HarmonyMethod(prefix), null);
                        Log("MainMenuUI.Start フック成功");
                    }

                    MethodInfo mOnGUI = AccessTools.Method(mainUiType, "OnGUI");
                    if (mOnGUI != null)
                    {
                        MethodInfo onGuiPrefix = AccessTools.Method(typeof(Loader), "MainMenuUI_OnGUI_Prefix");
                        harmony.Patch(mOnGUI, new HarmonyMethod(onGuiPrefix), null);
                        Log("MainMenuUI.OnGUI フック成功");
                    }

                    MethodInfo mButton = AccessTools.Method(mainUiType, "MenuButton", new Type[] { typeof(Rect), typeof(string), typeof(bool) });
                    if (mButton != null)
                    {
                        MethodInfo btnPrefix = AccessTools.Method(typeof(Loader), "MenuButton_Prefix");
                        harmony.Patch(mButton, new HarmonyMethod(btnPrefix), null);
                        Log("MainMenuUI.MenuButton フック成功");
                    }
                }

                Type itemCatType = asm.GetType("GoldDigger.ItemCatalog");
                if (itemCatType != null)
                {
                    MethodInfo mForFloor = AccessTools.Method(itemCatType, "ForFloor", new Type[] { typeof(int) });
                    if (mForFloor != null)
                    {
                        MethodInfo postfix = AccessTools.Method(typeof(Loader), "ItemCatalog_ForFloor_Postfix");
                        harmony.Patch(mForFloor, null, new HarmonyMethod(postfix));
                        Log("ItemCatalog.ForFloor フック成功");
                    }
                }

                Type upCatType = asm.GetType("GoldDigger.UpgradeCatalog");
                if (upCatType != null)
                {
                    MethodInfo mCreateDefaults = AccessTools.Method(upCatType, "CreateDefaults");
                    if (mCreateDefaults != null)
                    {
                        MethodInfo postfix = AccessTools.Method(typeof(Loader), "UpgradeCatalog_CreateDefaults_Postfix");
                        harmony.Patch(mCreateDefaults, null, new HarmonyMethod(postfix));
                        Log("UpgradeCatalog.CreateDefaults フック成功");
                    }
                }

                Type vaultUiType = asm.GetType("GoldDigger.VaultUI");
                if (vaultUiType != null)
                {
                    MethodInfo mEnsure = AccessTools.Method(vaultUiType, "Ensure");
                    if (mEnsure != null)
                    {
                        MethodInfo postfix = AccessTools.Method(typeof(Loader), "VaultUI_Ensure_Postfix");
                        harmony.Patch(mEnsure, null, new HarmonyMethod(postfix));
                        Log("VaultUI.Ensure フック成功");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("フック適用エラー: " + ex);
            }
        }

        public static void MainMenuUI_Start_Prefix(object __instance)
        {
            if (_engineInitialized) return;
            _engineInitialized = true;

            try
            {
                Log("=== Unity初期化完了: 日本語フォントを構築中 ===");
                InitializeFonts();
            }
            catch (Exception ex)
            {
                Log("MainMenuUI_Start_Prefix エラー: " + ex);
            }
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
                                GUIStyle style = f.GetValue(__instance) as GUIStyle;
                                if (style != null && style.font != CustomFontRegular)
                                {
                                    style.font = CustomFontRegular;
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
            string trimmed = label.Trim();
            string translated;
            if (UiTexts.TryGetValue(trimmed, out translated))
            {
                label = translated;
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
                        if (ext == ".ttf" || ext == ".otf")
                        {
                            int res = AddFontResourceEx(f, FR_PRIVATE, IntPtr.Zero);
                            if (res > 0)
                            {
                                string fname = Path.GetFileNameWithoutExtension(f);
                                Log(string.Format("ローカルフォント登録: {0}", Path.GetFileName(f)));
                                if (!candidates.Contains(fname)) candidates.Add(fname);
                            }
                        }
                    }
                }
            }

            foreach (var s in new string[] { "Meiryo", "Yu Gothic", "MS Gothic", "Arial Unicode MS" })
            {
                if (!candidates.Contains(s)) candidates.Add(s);
            }

            Log("フォント解決候補: " + string.Join(", ", candidates.ToArray()));

            CustomFontRegular = Font.CreateDynamicFontFromOSFont(candidates.ToArray(), 18);
            CustomFontBold = Font.CreateDynamicFontFromOSFont(candidates.ToArray(), 20);
            CustomFontDisplay = Font.CreateDynamicFontFromOSFont(candidates.ToArray(), 24);

            string name = (CustomFontRegular != null) ? CustomFontRegular.name : "null";
            Log("フォント適用成功: " + name);
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
                if (fBold != null) fBold.SetValue(__instance, CustomFontBold);
                if (fDisp != null) fDisp.SetValue(__instance, CustomFontDisplay);
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
                        string trName;
                        if (ItemNames.TryGetValue(id, out trName) && fName != null)
                        {
                            fName.SetValue(item, trName);
                        }
                        string trDesc;
                        if (ItemDescriptions.TryGetValue(id, out trDesc) && fDesc != null)
                        {
                            fDesc.SetValue(item, trDesc);
                        }
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
                        string trDesc;
                        if (UpgradeDescriptions.TryGetValue(id, out trDesc))
                        {
                            fDesc.SetValue(up, trDesc);
                        }
                    }
                }
            }
            catch { }
        }
    }
}
