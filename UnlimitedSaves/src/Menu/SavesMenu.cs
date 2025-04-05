using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using RWCustom;
using UnityEngine;
using global::Menu;
using Kittehface.Framework20;
using Menu.Remix;
using Menu.Remix.MixedUI;
using MoreSlugcats;
using DyeableRect = MoreSlugcats.DyeableRect;

namespace UnlimitedSaves.Menu;

using Menu = global::Menu;
using ProgressionLoadResult = PlayerProgression.ProgressionLoadResult;

public class SavesMenu : Menu.Menu, SelectOneButton.SelectOneButtonOwner, CheckBox.IOwnCheckBox, Slider.ISliderOwner {
    public static readonly ProcessManager.ProcessID id = new($"{nameof(SavesMenu)}+{Plugin.Id}", true);

    private class DummyOi : OptionInterface;

    private readonly FSprite _darkSprite;
    private readonly SimpleButton _backButton;
    private readonly SimpleButton _backupsButton;
    private bool _lastPauseButton;
    private bool _exiting;

    private bool progressionBusy => _waitingOnProgressionLoaded || !manager.rainWorld.progression.progressionLoaded;

    private const int MaxShownSaveButtons = 16;
    private const float SaveButtonWidth = 220f;
    private SelectOneButton[] _saveButtons = [];
    private int _saveButtonsScroll;
    private static readonly Slider.SliderID saveButtonsScrollBarId = new($"SaveButtonsScroll+{Plugin.Id}", true);
    private int _leavingSaveSlot;
    private bool _waitingOnProgressionLoaded;
    private bool _reportCorruptedDialogDisplaying;

    private readonly HoldButton _resetButton;
    private readonly MenuLabel _resetWarningText;
    private readonly MenuLabel _deleteWarningText;
    private int _resetWarningTextCounter;
    private float _resetWarningTextAlpha;
    private readonly CheckBox _deleteCheckbox;

    // TODO: options to reset specific campaigns and other save data like challenges expeditions etc
    private bool _shouldDeleteSave;

    private class SlugcatInfoCard : PositionedMenuObject {
        public const float Width = 100f * Scale + 10f + 274f; // text position + size
        public const float Height = 100f * Scale;
        private const float Scale = 0.6f;

        public SlugcatInfoCard(Menu.Menu menu, MenuObject owner, Vector2 pos, SlugcatStats.Name slugcat) :
            base(menu, owner, pos) {
            Plugin.logger.LogInfo($"loading data for {slugcat} in slot {menu.manager.rainWorld.options.saveSlot}");
            SlugcatSelectMenu.SaveGameData data = SlugcatSelectMenu.MineForSaveData(menu.manager, slugcat);

            DyeableRect illustrationRect = new(menu, this, new Vector2(0f, -100f) * Scale,
                new Vector2(100f, 100f) * Scale, false);
            // ReSharper disable once ConvertIfStatementToSwitchStatement
            if (data.ascended && data.altEnding)
                illustrationRect.color = Color.magenta;
            else if (data.ascended)
                illustrationRect.color = Color.yellow;
            else if (data.altEnding)
                illustrationRect.color = Color.cyan;
            MenuIllustration illustration = new(menu, this, "", slugcat.value switch {
                nameof(SlugcatStats.Name.Yellow) => "multiplayerportrait11-yellow",
                nameof(SlugcatStats.Name.White) => "multiplayerportrait01-white",
                nameof(SlugcatStats.Name.Red) => data.redsDeath ? "multiplayerportrait20-red" : "multiplayerportrait21-red",
                nameof(MoreSlugcatsEnums.SlugcatStatsName.Rivulet) => "multiplayerportrait41-rivulet",
                nameof(MoreSlugcatsEnums.SlugcatStatsName.Artificer) => "multiplayerportrait41-artificer",
                nameof(MoreSlugcatsEnums.SlugcatStatsName.Spear) => "multiplayerportrait41-spear",
                nameof(MoreSlugcatsEnums.SlugcatStatsName.Gourmand) => "multiplayerportrait41-gourmand",
                nameof(MoreSlugcatsEnums.SlugcatStatsName.Saint) => "multiplayerportrait41-saint",
                _ => "multiplayerportrait02"
            }, new Vector2(50f, -50f) * Scale, false, true);
            illustration.size *= Scale;
            illustration.sprite.scale *= Scale;
            illustrationRect.subObjects.Add(illustration);
            subObjects.Add(illustrationRect);

            IntVector2 maxFood = SlugcatStats.SlugcatFoodMeter(slugcat);
            string karmaData = $"{data.karma + 1}{(data.karmaReinforced ? "X" : "")}/{data.karmaCap + 1}";
            string foodData = $"{data.food}/{maxFood.y}/{maxFood.x}";

            string defaultMiscData = $"{(data.hasGlow ? "glow" : "-")}  {(data.hasMark ? "mark" : "-")}  {(data.moonGivenRobe ? "robe" : "-")}";
            string miscData = slugcat.value switch {
                nameof(SlugcatStats.Name.Red) => $"{defaultMiscData}  {(data.redsExtraCycles ? "+cycles" : "-")}  {(data.redsDeath ? "dead" : "-")}",
                nameof(MoreSlugcatsEnums.SlugcatStatsName.Rivulet) => $"{defaultMiscData}  {(data.pebblesEnergyTaken ? "orb" : "-")}",
                nameof(MoreSlugcatsEnums.SlugcatStatsName.Artificer) => $"{defaultMiscData}  {(data.hasRobo ? "robo" : "-")}",
                _ => defaultMiscData
            };

            TimeSpan aliveTime = TimeSpan.FromSeconds(data.gameTimeAlive);
            TimeSpan deadTime = TimeSpan.FromSeconds(data.gameTimeDead);
            string alive = SpeedRunTimer.TimeFormat(aliveTime);
            string dead = SpeedRunTimer.TimeFormat(deadTime);
            string total = SpeedRunTimer.TimeFormat(aliveTime + deadTime);

            string text = $"""
                          k{karmaData}  f{foodData}  c{data.cycle}  s{data.shelterName}
                          {miscData}
                          a{alive} + d{dead} = {total}
                          """;

            MenuLabel label = new(menu, this, text, new Vector2(100f * Scale + 10f, -100f * Scale),
                new Vector2(0f, 100f * Scale), false) { label = { alignment = FLabelAlignment.Left } };
            subObjects.Add(label);
        }
    }

    private readonly OpTextBox _saveNameInput;
    private readonly MenuLabel _saveNameInputLabel;

    private const int MaxShownSlugcatInfoCards = 8;
    private readonly List<SlugcatInfoCard> _slugcatInfoCards = [];
    private int _slugcatInfoCardsScroll;
    private static readonly Slider.SliderID slugcatInfoCardsScrollBarId = new($"InfoCardsScroll+{Plugin.Id}", true);

    private const float ScrollBarPadding = 8f;

    public SavesMenu(ProcessManager manager) : base(manager, id) {
        pages.Add(new Page(this, null, "main", 0));
        scene = new InteractiveMenuScene(this, pages[0], manager.rainWorld.options.subBackground);
        pages[0].subObjects.Add(scene);

        _darkSprite = new FSprite("pixel") {
            color = new Color(0f, 0f, 0f),
            anchorX = 0f,
            anchorY = 0f,
            scaleX = 1368f,
            scaleY = 770f,
            x = -1f,
            y = -1f,
            alpha = 0.85f
        };
        pages[0].Container.AddChild(_darkSprite);

        mySoundLoopID = SoundID.MENU_Main_Menu_LOOP;

        _backButton = new SimpleButton(this, pages[0], Translate("BACK"), "BACK", new Vector2(200f, 50f),
            new Vector2(110f, 30f));
        pages[0].subObjects.Add(_backButton);
        backObject = _backButton;

        _backupsButton = new SimpleButton(this, pages[0], Translate("BACKUPS"), "BACKUPS", new Vector2(320f, 50f),
            new Vector2(110f, 30f));
        pages[0].subObjects.Add(_backupsButton);

        VerticalSlider saveButtonsScrollBar = new(this, pages[0], "",
            new Vector2(
                1366f / 2f + SaveButtonWidth / 2f + 5f,
                680f - (MaxShownSaveButtons - 1) * 40f - 30f + ScrollBarPadding
            ),
            new Vector2(
                0f,
                (MaxShownSaveButtons - 1) * 40f + 30f - 20f - ScrollBarPadding * 2f
            ),
            saveButtonsScrollBarId, true
        );
        pages[0].subObjects.Add(saveButtonsScrollBar);

        _resetButton = new HoldButton(this, pages[0],
            Translate("RESET PROGRESS").Replace("<LINE>", "\r\n"), "RESET PROGRESS",
            new Vector2(1366f / 2f + SaveButtonWidth / 2f + 20f + 55f + 15f + 20f, 680f - 55f), 400f
        );
        pages[0].subObjects.Add(_resetButton);

        _resetWarningText = new MenuLabel(this, pages[0],
            Translate("WARNING!<LINE>This will reset all progress in the selected save slot,<LINE>including map exploration. Unlocked arenas and<LINE>sandbox items are retained.").Replace("<LINE>", "\r\n"),
            new Vector2(_resetButton.pos.x + 55f + 15f + 20f, _resetButton.pos.y),
            new Vector2(0f, 0f),
            false
        ) { label = { alignment = FLabelAlignment.Left } };
        pages[0].subObjects.Add(_resetWarningText);

        _deleteWarningText = new MenuLabel(this, pages[0],
            Translate("WARNING!!!<LINE>This will fully erase the selected save slot<LINE>without a way to recover it.<LINE>Proceed with caution!").Replace("<LINE>", "\r\n"),
            _resetWarningText.pos,
            _resetWarningText.size,
            false
        ) { label = { alignment = _resetWarningText.label.alignment } };
        pages[0].subObjects.Add(_deleteWarningText);

        _deleteCheckbox = new CheckBox(this, pages[0], this,
            new Vector2(_resetButton.pos.x - 55f - 15f, _resetButton.pos.y - 55f - 15f - 30f), 50f,
            Translate("Delete save"), "DELETE SAVE", true
        );
        _deleteCheckbox.label.label.alignment = FLabelAlignment.Left;
        pages[0].subObjects.Add(_deleteCheckbox);

        MenuTabWrapper tabWrapper = new(this, pages[0]);
        pages[0].subObjects.Add(tabWrapper);

        _saveNameInput = new OpTextBox(
            new DummyOi().config.Bind(null, ""),
            new Vector2(70f + 60f, 680f - 24f),
            SaveButtonWidth
        );
        _ = new UIelementWrapper(tabWrapper, _saveNameInput);
        _saveNameInput.accept = OpTextBox.Accept.StringASCII;
        _saveNameInput.allowSpace = true;

        _saveNameInputLabel = new MenuLabel(this, pages[0],
            $"{Translate("Name")}:",
            new Vector2(70f, 680f - 4f),
            Vector2.zero,
            false
        );
        _saveNameInputLabel.label.SetAnchor(0f, 1f);
        _saveNameInputLabel.label.alignment = FLabelAlignment.Left;
        pages[0].subObjects.Add(_saveNameInputLabel);

        VerticalSlider slugcatInfoCardsScrollBar = new(this, pages[0], "",
            new Vector2(
                _saveNameInputLabel.pos.x + SlugcatInfoCard.Width + 5f,
                _saveNameInput.pos.y - 10f - (MaxShownSlugcatInfoCards - 1) * 70f - SlugcatInfoCard.Height + ScrollBarPadding
            ),
            new Vector2(
                0f,
                (MaxShownSlugcatInfoCards - 1) * 70f + SlugcatInfoCard.Height - 20f - ScrollBarPadding * 2f
            ),
            slugcatInfoCardsScrollBarId, true
        );
        pages[0].subObjects.Add(slugcatInfoCardsScrollBar);

        string saveDir = Application.persistentDataPath;
        if (manager.rainWorld.progression.saveFileDataInMemory.overrideBaseDir is not null)
            saveDir = manager.rainWorld.progression.saveFileDataInMemory.overrideBaseDir;
        if (!File.Exists(Path.Combine(saveDir, manager.rainWorld.progression.saveFileDataInMemory.filename)))
            SetCurrentlySelectedOfSeries("SaveSlot", 0);

        UpdateButtons();
        UpdateInfoCards();

        // puts it in the middle
        _saveButtonsScroll = MaxShownSaveButtons / 2 - manager.rainWorld.options.saveSlot;
        _slugcatInfoCardsScroll = MaxShownSlugcatInfoCards / 2;
    }

    private string GetSaveSlotName(int slot) =>
        Plugin.saveNames.TryGetValue(slot, out string name) && !string.IsNullOrWhiteSpace(name) ?
            name : $"{Translate("SAVE SLOT")} {slot + 1}";

    public int GetCurrentlySelectedOfSeries(string series) => series switch {
        "SaveSlot" => manager.rainWorld.options.saveSlot,
        _ => 0
    };

    public void SetCurrentlySelectedOfSeries(string series, int to) {
        switch (series) {
            case "SaveSlot":
                if (manager.rainWorld.options.saveSlot == to)
                    break;
                _leavingSaveSlot = manager.rainWorld.options.saveSlot;
                manager.rainWorld.options.saveSlot = to;
                manager.rainWorld.progression.Destroy(_leavingSaveSlot);
                manager.rainWorld.progression = new PlayerProgression(manager.rainWorld, true, false);
                _waitingOnProgressionLoaded = true;
                _saveNameInput.value =
                    Plugin.saveNames.TryGetValue(manager.rainWorld.options.saveSlot, out string name) ? name ?? "" : "";
                break;
        }
    }

    public bool GetChecked(CheckBox box) {
        if (box == _deleteCheckbox) {
            return _shouldDeleteSave;
        }
        return false;
    }

    public void SetChecked(CheckBox box, bool c) {
        if (box == _deleteCheckbox) {
            _shouldDeleteSave = c;
        }
    }

    public override float ValueOfSlider(Slider slider) {
        if (slider.ID == saveButtonsScrollBarId)
            return 1f - _saveButtonsScroll / Math.Min(MaxShownSaveButtons - _saveButtons.Length, 0f);
        if (slider.ID == slugcatInfoCardsScrollBarId)
            return 1f - _slugcatInfoCardsScroll / Math.Min(MaxShownSlugcatInfoCards - _slugcatInfoCards.Count, 0f);
        return 0f;
    }

    public override void SliderSetValue(Slider slider, float f) {
        if (slider.ID == saveButtonsScrollBarId) {
            _saveButtonsScroll = Mathf.FloorToInt((1f - f) * Math.Min(MaxShownSaveButtons - _saveButtons.Length, 0f));
            return;
        }
        if (slider.ID == slugcatInfoCardsScrollBarId) {
            _slugcatInfoCardsScroll =
                Mathf.FloorToInt((1f - f) * Math.Min(MaxShownSlugcatInfoCards - _slugcatInfoCards.Count, 0f));
            return;
        }
    }

    public override void Update() {
        base.Update();

        UpdateHandleBackButton();
        UpdateHandleSaveSlotChange();
        UpdateHandleResetButton();
        UpdateHandleSaveName();

        _backButton.buttonBehav.greyedOut = progressionBusy;

        foreach (SelectOneButton button in _saveButtons)
            button.buttonBehav.greyedOut = progressionBusy;
        _resetButton.buttonBehav.greyedOut = progressionBusy;
    }

    public override void GrafUpdate(float timeStacker) {
        base.GrafUpdate(timeStacker);

        // vanilla scroll handling is ass because it does it in update. incredible.
        // i do it in grafupdate because i do not want to go insane using my own mod.

        if (manager.menuesMouseMode) {
            if (mousePosition.x is >= 1366f / 2f - SaveButtonWidth / 2f and <= 1366f / 2f + SaveButtonWidth / 2f)
                _saveButtonsScroll += (int)Input.mouseScrollDelta.y;
            if (mousePosition.x >= _saveNameInputLabel.pos.x &&
                mousePosition.x <= _saveNameInputLabel.pos.x + SlugcatInfoCard.Width)
                _slugcatInfoCardsScroll += (int)Input.mouseScrollDelta.y;
        }

        for (int i = 0; i < _saveButtons.Length; i++) {
            int index = i + _saveButtonsScroll;
            if (_saveButtons[i].Selected && index is < 0 or >= MaxShownSaveButtons)
                _saveButtonsScroll = Mathf.Clamp(index, 0, 15) - i;
        }

        _saveButtonsScroll = Mathf.Clamp(_saveButtonsScroll, Math.Min(MaxShownSaveButtons - _saveButtons.Length, 0), 0);
        _slugcatInfoCardsScroll = Mathf.Clamp(
            _slugcatInfoCardsScroll, Math.Min(MaxShownSlugcatInfoCards - _slugcatInfoCards.Count, 0), 0
        );

        for (int i = 0; i < _saveButtons.Length; i++) {
            SelectOneButton button = _saveButtons[i];
            int index = i + _saveButtonsScroll;
            // there is also no proper cutoff container
            // (except OpScrollBox, which i cant use for my use case and it sucks anyway)
            // and i cba to implement that myself
            button.pos.y = index switch {
                < 0 => 768f + button.size.y * 2f,
                >= MaxShownSaveButtons => -button.size.y * 2f,
                _ => 680f - index * 40f - button.size.y
            };
            button.lastPos.y = button.pos.y;
        }

        for (int i = 0; i < _slugcatInfoCards.Count; i++) {
            SlugcatInfoCard card = _slugcatInfoCards[i];
            int index = i + _slugcatInfoCardsScroll;
            card.pos.y = index switch {
                < 0 => 768f + SlugcatInfoCard.Height * 2f,
                >= MaxShownSlugcatInfoCards => -SlugcatInfoCard.Height * 2f,
                _ => _saveNameInput.pos.y - 10f - index * 70f
            };
            card.lastPos.y = card.pos.y;
        }
    }

    private void UpdateHandleBackButton() {
        bool pause = RWInput.CheckPauseButton(0);
        if (pause && !_lastPauseButton && manager.dialog == null && !_backButton.buttonBehav.greyedOut)
            Singal(_backButton, _backButton.signalText);
        _lastPauseButton = pause;
    }

    private void UpdateHandleSaveSlotChange() {
        if (!_waitingOnProgressionLoaded || !manager.rainWorld.progression.progressionLoaded)
            return;
        ProgressionLoadResult res = manager.rainWorld.progression.progressionLoadedResult;
        if (res is ProgressionLoadResult.SUCCESS_CREATE_NEW_FILE
            or ProgressionLoadResult.SUCCESS_LOAD_EXISTING_FILE
            or ProgressionLoadResult.ERROR_SAVE_DATA_MISSING)
            HandleSaveSlotChangeSucceeded(res);
        else
            HandleSaveSlotChangeFailed(res);
    }

    private void UpdateHandleResetButton() {
        if (_resetButton.FillingUp) {
            _resetWarningTextCounter++;
            _resetWarningTextAlpha = Custom.LerpAndTick(_resetWarningTextAlpha, 1f, 0.08f, 0.025f);
        }
        else {
            _resetWarningTextAlpha = Custom.LerpAndTick(_resetWarningTextAlpha, 0f, 0.08f, 0.05f);
        }
        MenuLabel resetWarningText = _shouldDeleteSave ? _deleteWarningText : _resetWarningText;
        resetWarningText.label.alpha =
            (0.7f + 0.3f * Mathf.Sin(_resetWarningTextCounter / 40f * Mathf.PI * 2f)) * _resetWarningTextAlpha;
        resetWarningText.label.color = Color.Lerp(
            new Color(1f, 0f, 0f), new Color(1f, 1f, 1f),
            Mathf.Pow(0.5f + 0.5f * Mathf.Sin(_resetWarningTextCounter / 40f * Mathf.PI * 2f), 2f)
        );
        MenuLabel otherResetWarningText = !_shouldDeleteSave ? _deleteWarningText : _resetWarningText;
        otherResetWarningText.label.alpha = 0f;
    }

    private void UpdateHandleSaveName() {
        int slot = manager.rainWorld.options.saveSlot;
        if (!_saveNameInput._KeyboardOn) {
            _saveNameInput.label.text = GetSaveSlotName(slot);
            return;
        }
        _saveNameInput.label.text = Plugin.saveNames.TryGetValue(slot, out string name) ? name ?? "" : "";
        if (string.IsNullOrWhiteSpace(_saveNameInput.value))
            Plugin.saveNames.Remove(slot);
        else
            Plugin.saveNames[slot] = _saveNameInput.value;
        _saveButtons[slot].menuLabel.text = GetSaveSlotName(slot);
    }

    private void UpdateButtons(bool noCreate = false) {
        string[] results = Directory.GetFiles(UserData.GetPersistentDataPath(), "sav*");

        HashSet<int> existing = [ 0 ];
        int maxSlot = manager.rainWorld.options.saveSlot;
        foreach (string result in results) {
            if (!int.TryParse(Path.GetFileName(result).Substring(3), out int slot))
                continue;
            --slot;
            maxSlot = Math.Max(slot, maxSlot);
            existing.Add(slot);
        }

        if (!noCreate) {
            foreach (SelectOneButton button in _saveButtons) {
                pages[0].RemoveSubObject(button);
                button.RemoveSprites();
            }
            _saveButtons = new SelectOneButton[maxSlot + 2];
        }

        Vector2 size = new(SaveButtonWidth, 30f);
        for (int i = 0; i < _saveButtons.Length; i++) {
            if (!noCreate) {
                Vector2 pos = new(1366f / 2f - size.x / 2f, 680f - i * 40f - size.y);
                string name = GetSaveSlotName(i);
                _saveButtons[i] = new SelectOneButton(this, pages[0], name, "SaveSlot", pos, size, _saveButtons, i);
                pages[0].subObjects.Add(_saveButtons[i]);
            }

            _saveButtons[i].labelColor = existing.Contains(i) ?
                MenuColor(MenuColors.MediumGrey) :
                new HSLColor(120f / 360f, 0.65f, 0.5f);
        }
    }

    private void UpdateInfoCards() {
        foreach (SlugcatInfoCard card in _slugcatInfoCards) {
            pages[0].RemoveSubObject(card);
            card.RemoveSprites();
        }
        _slugcatInfoCards.Clear();

        string[] progLines = manager.rainWorld.progression.GetProgLinesFromMemory();
        int index = 0;
        foreach (string progLine in progLines) {
            string[] split = Regex.Split(progLine, "<progDivB>");
            if (split.Length != 2 || split[0] != "SAVE STATE")
                continue;
            SlugcatStats.Name slugcat = BackwardsCompatibilityRemix.ParseSaveNumber(split[1]);
            SlugcatInfoCard card = new(this, pages[0],
                new Vector2(_saveNameInputLabel.pos.x, _saveNameInput.pos.y - 10f - index++ * 70f), slugcat);
            _slugcatInfoCards.Add(card);
            pages[0].subObjects.Add(card);
        }
    }

    private void HandleSaveSlotChangeSucceeded(ProgressionLoadResult result) {
        Plugin.logger.LogDebug($"succeeded changing save slot: {result}");
        _waitingOnProgressionLoaded = false;
        bool isLast = _saveButtons[_saveButtons.Length - 1].AmISelected;
        UpdateButtons(!isLast);
        UpdateInfoCards();
        if (isLast)
            _saveButtonsScroll = MaxShownSaveButtons - _saveButtons.Length;
    }

    private void HandleSaveSlotChangeFailed(ProgressionLoadResult result) {
        Plugin.logger.LogError(
            $"failed changing save slot: {result}, {manager.rainWorld.progression.SaveDataReadFailureError}");

        if (_reportCorruptedDialogDisplaying)
            return;
        _reportCorruptedDialogDisplaying = true;

        string errorText = result.ToString();
        if (result == ProgressionLoadResult.ERROR_READ_FAILED &&
            manager.rainWorld.progression.SaveDataReadFailureError != null)
            errorText = $"{errorText}{Environment.NewLine}{manager.rainWorld.progression.SaveDataReadFailureError}";
        string text = manager.rainWorld.inGameTranslator.Translate("ps4_load_save_slot_load_failed")
            .Replace("{ERROR}", errorText);

        DialogNotify dialog = new(
            Custom.ReplaceLineDelimeters(text),
            DialogBoxNotify.CalculateDialogBoxSize(text, false),
            manager.rainWorld.processManager,
            () => {
                _reportCorruptedDialogDisplaying = false;
                if (_leavingSaveSlot < 0) {
                    _waitingOnProgressionLoaded = false;
                    return;
                }
                manager.rainWorld.options.saveSlot = _leavingSaveSlot;
                manager.rainWorld.progression.Destroy();
                manager.rainWorld.progression = new PlayerProgression(manager.rainWorld, true, false);
                _waitingOnProgressionLoaded = true;
                _leavingSaveSlot = -1;
                _saveNameInput.value =
                    Plugin.saveNames.TryGetValue(manager.rainWorld.options.saveSlot, out string name) ? name ?? "" : "";
            }
        );
        manager.rainWorld.processManager.ShowDialog(dialog);
    }

    public override void Singal(MenuObject sender, string message) {
        if (_exiting || progressionBusy)
            return;
        switch (message) {
            case "RESET PROGRESS" when !_shouldDeleteSave:
                ResetSave();
                break;
            case "RESET PROGRESS" when _shouldDeleteSave:
                DeleteSave();
                break;
            case "BACKUPS":
                _exiting = true;
                manager.RequestMainProcessSwitch(ProcessManager.ProcessID.BackupManager);
                PlaySound(SoundID.MENU_Switch_Page_In);
                manager.rainWorld.options.Save();
                On.ProcessManager.RequestMainProcessSwitch_ProcessID_float += BackupsMenuBackOverride;
                break;
            case "BACK":
                _exiting = true;
                manager.RequestMainProcessSwitch(ProcessManager.ProcessID.MainMenu);
                PlaySound(SoundID.MENU_Switch_Page_Out);
                manager.rainWorld.options.Save();
                break;
        }
    }

    private static void BackupsMenuBackOverride(On.ProcessManager.orig_RequestMainProcessSwitch_ProcessID_float orig,
        ProcessManager self, ProcessManager.ProcessID id, float fadeOutSeconds) {
        if (id != ProcessManager.ProcessID.OptionsMenu) {
            orig(self, id, fadeOutSeconds);
            return;
        }
        Plugin.logger.LogInfo("redirecting options to saves because backups was entered from saves");
        On.ProcessManager.RequestMainProcessSwitch_ProcessID_float -= BackupsMenuBackOverride;
        orig(self, SavesMenu.id, fadeOutSeconds);
    }

    private void ResetSave() {
        Plugin.logger.LogInfo("resetting save");
        manager.rainWorld.progression.WipeAll();
        manager.RequestMainProcessSwitch(id);
        PlaySound(SoundID.MENU_Switch_Page_In);
    }

    private void DeleteSave() {
        Plugin.logger.LogInfo("deleting save");
        Plugin.saveNames.Remove(manager.rainWorld.options.saveSlot);
        Plugin.logger.LogDebug("deleting expCore (expedition)");
        string path = Path.Combine(Application.persistentDataPath, $"expCore{manager.rainWorld.options.saveSlot + 1}");
        if (File.Exists(path))
            File.Delete(path);
        Plugin.logger.LogDebug("deleting SJ (saint journey)");
        path = Path.Combine(Application.persistentDataPath, $"SJ_{manager.rainWorld.options.saveSlot}");
        if (Directory.Exists(path))
            Directory.Delete(path, true);
        Plugin.logger.LogDebug("deleting sav (regular save)");
        manager.rainWorld.progression.saveFileDataInMemory.OnDeleteCompleted += OnDeleteSaveCompleted;
        manager.rainWorld.progression.saveFileDataInMemory.Delete();
    }

    private void OnDeleteSaveCompleted(UserData.File file, UserData.Result result) {
        Plugin.logger.LogDebug("deletion complete");
        manager.rainWorld.progression.saveFileDataInMemory.OnDeleteCompleted -= OnDeleteSaveCompleted;
        _exiting = true;
        manager.RequestMainProcessSwitch(id);
        PlaySound(SoundID.MENU_Switch_Page_In);
    }

    public override string UpdateInfoText() {
        if (selectedObject == _backButton)
            return Translate("Back to main menu");
        if (selectedObject == _backupsButton)
            return Translate("backups_description");
        if (selectedObject == _resetButton)
            return Translate("Hold down to wipe your save slot and start over");
        if (selectedObject == _deleteCheckbox)
            return Translate("Enable to fully delete the selected save slot instead of wiping it");
        // shut up im too lazy to implement this properly rn
        if (selectedObject is SelectOneButton oneButt && oneButt.labelColor.rgb != MenuColor(MenuColors.MediumGrey).rgb)
            return Translate("Create new save file");
        return base.UpdateInfoText();
    }

    public override void ShutDownProcess() {
        base.ShutDownProcess();
        _darkSprite.RemoveFromContainer();
    }
}
