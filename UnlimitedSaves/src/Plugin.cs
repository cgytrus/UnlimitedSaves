using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using Menu;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using UnityEngine;
using UnlimitedSaves.Menu;

namespace UnlimitedSaves;

[BepInAutoPlugin("cgytrus.unlimited-saves")]
public partial class Plugin : BaseUnityPlugin {
    private static Plugin? _instance;

    public static ManualLogSource logger => _instance!.Logger;
    public static Dictionary<int, string> saveNames { get; } = new();

    private Plugin() => _instance = this;

    private void Awake() {
        IL.Menu.MainMenu.AddMainMenuButton += il => {
            try {
                ILCursor cursor = new(il);
                int maxCount = 0;
                if (!cursor.TryGotoNext(code => code.MatchStloc(1)) ||
                    !cursor.TryGotoPrev(MoveType.After, code => code.MatchLdcI4(out maxCount))) {
                    Logger.LogWarning("failed to increase main menu vertical buttons limit!");
                    return;
                }
                cursor.Emit(OpCodes.Pop);
                cursor.Emit(OpCodes.Ldc_I4, maxCount + 1);
            }
            catch (Exception ex) {
                Logger.LogWarning("failed to increase main menu vertical buttons limit!");
                Logger.LogError(ex);
            }
        };

        On.Menu.MainMenu.ctor += (orig, self, manager, bkg) => {
            orig(self, manager, bkg);
            float buttonWidth = MainMenu.GetButtonWidth(self.CurrLang);
            SimpleButton button = new(
                self, self.pages[0], self.Translate("SAVES"), "SAVES",
                new Vector2(1366f / 2f - buttonWidth / 2f, 0f), new Vector2(buttonWidth, 30f)
            );
            self.AddMainMenuButton(button, () => {
                self.manager.RequestMainProcessSwitch(SavesMenu.id);
                self.PlaySound(SoundID.MENU_Switch_Page_In);
            }, 2);
        };

        On.ProcessManager.PostSwitchMainProcess += (orig, self, id) => {
            if (id == SavesMenu.id)
                self.currentMainLoop = new SavesMenu(self);
            orig(self, id);
        };

        // vanilla backups only a select few hardcoded files, so we take the matters in our own hands
        On.PlayerProgression.CreateCopyOfSaves_bool += BackupFix;
        On.Menu.BackupManager.RestoreSaveFile += RestoreFix;

        On.Options.ApplyOption += LoadSaveName;
        On.Options.ToString += SaveSaveNames;
    }

    private static void BackupFix(On.PlayerProgression.orig_CreateCopyOfSaves_bool orig, PlayerProgression self,
        bool userCreated) {
        orig(self, userCreated);
        double totalSeconds = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        string dir = Path.Combine(Application.persistentDataPath, "backup", $"{(long)totalSeconds}_{DateTime.Now:yyyy-MM-dd_HH-mm}");
        if (userCreated)
            dir += "_USR";
        logger.LogInfo("backing up extra saves");
        foreach (string filePath in Directory.GetFiles(Application.persistentDataPath)) {
            string file = Path.GetFileName(filePath);
            // ignore files already backed up by vanilla
            bool isSav = file.StartsWith("sav", StringComparison.Ordinal);
            bool isExpCore = file.StartsWith("expCore", StringComparison.Ordinal);
            bool isExp = !isExpCore && file.StartsWith("exp", StringComparison.Ordinal);
            isSav = isSav && int.TryParse(file.Substring(3), out int slot) && slot > 3;
            isExpCore = isExpCore && int.TryParse(file.Substring(7), out slot) && slot > 3;
            isExp = isExp && int.TryParse(file.Substring(3), out slot) && slot > 1;
            if (!isSav && !isExpCore && !isExp)
                continue;
            self.CopySaveFile(file, dir);
        }
    }

    private static void RestoreFix(On.Menu.BackupManager.orig_RestoreSaveFile orig, BackupManager self,
        string sourceName) {
        orig(self, sourceName);
        // TODO: more future proof way to do this
        if (sourceName != "exp1")
            return;
        logger.LogInfo("restoring extra saves");
        foreach (string filePath in Directory.GetFiles(self.backupDirectories[self.selectedBackup])) {
            string file = Path.GetFileName(filePath);
            // ignore files already restored by vanilla
            bool isSav = file.StartsWith("sav", StringComparison.Ordinal);
            bool isExpCore = file.StartsWith("expCore", StringComparison.Ordinal);
            bool isExp = !isExpCore && file.StartsWith("exp", StringComparison.Ordinal);
            isSav = isSav && int.TryParse(file.Substring(3), out int slot) && slot > 3;
            isExpCore = isExpCore && int.TryParse(file.Substring(7), out slot) && slot > 3;
            isExp = isExp && int.TryParse(file.Substring(3), out slot) && slot > 1;
            if (!isSav && !isExpCore && !isExp)
                continue;
            orig(self, file);
        }
    }

    private static bool LoadSaveName(On.Options.orig_ApplyOption orig, Options self, string[] split) {
        bool unrecognized = orig(self, split);
        if (!unrecognized)
            return false;
        string key = split[0];
        if (key != "unlimitedsaves:name")
            return true;
        string[] kv = split[1].Split(',');
        if (!int.TryParse(kv[0], out int slot))
            return true;
        logger.LogInfo($"loading save name for slot {slot}");
        try {
            string name = Base64Decode(kv[1]);
            saveNames[slot] = name;
        }
        catch { return true; }
        return false;
    }

    private static string SaveSaveNames(On.Options.orig_ToString orig, Options self) {
        string ret = orig(self);
        logger.LogInfo("saving save names");
        return saveNames.Aggregate(ret,
            (current, kv) => current + $"unlimitedsaves:name<optB>{kv.Key},{Base64Encode(kv.Value)}<optA>");
    }

    // https://stackoverflow.com/a/11743162
    private static string Base64Encode(string plainText) => Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

    private static string Base64Decode(string base64EncodedData) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(base64EncodedData));
}
