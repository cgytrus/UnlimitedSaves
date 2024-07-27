using System;
using BepInEx;
using BepInEx.Configuration;
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
    }
}
