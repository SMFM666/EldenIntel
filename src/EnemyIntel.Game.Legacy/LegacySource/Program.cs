using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows.Forms;

namespace EnemyIntelReader
{
    internal enum LinkState
    {
        Offline,
        Linking,
        Linked
    }

    internal sealed class MainForm : Form
    {
        private const string ProcessName = "eldenring";
        private const string LockOnPattern =
            "48 8B 48 08 49 89 8D B0 06 00 00 49 8B CE E8";
        private const string PatchedLockOnPattern =
            "E9 ?? ?? ?? ?? 90 90 90 90 90 90 49 8B CE E8";
        private const string WorldChrManPattern =
            "48 8B 05 ?? ?? ?? ?? 48 85 C0 74 0F 48 39 88";
        private const string GameDataManPattern =
            "48 8B 05 ?? ?? ?? ?? 48 85 C0 74 05 48 8B 40 58 C3 C3";
        private const string FieldAreaPattern =
            "48 8B 3D ?? ?? ?? ?? 49 8B D8 48 8B F2 4C 8B F1 48 85 FF";
        private const int PlayerInventoryOffset = 0x5D0;
        private const int PlayerInventoryCapacity = 2688;
        private const int InventoryEntrySize = 0x18;
        private const int ItemHistoryDrawerCollapsedWidth = 46;
        private const int ItemHistoryDrawerExpandedWidth = 280;
        private const int FreecamDrawerCollapsedWidth = 46;
        private const int FreecamDrawerExpandedWidth = 268;
        private const int ResizeHitSize = 8;
        private const int WmNcHitTest = 0x0084;
        private const int HtClient = 1;
        private const int HtCaption = 2;
        private const int HtLeft = 10;
        private const int HtRight = 11;
        private const int HtTop = 12;
        private const int HtTopLeft = 13;
        private const int HtTopRight = 14;
        private const int HtBottom = 15;
        private const int HtBottomLeft = 16;
        private const int HtBottomRight = 17;
        private const int CsDropShadow = 0x00020000;

        private const string IconLayoutDirectory =
            @"D:\SteamLibrary\steamapps\common\ELDEN RING\Game\menu\hi\01_common-sblytbnd-dcx";

        private const string IconTextureDirectory =
            @"D:\SteamLibrary\steamapps\common\ELDEN RING\Game\menu\hi\01_common-tpf-dcx";

        private static readonly string[] LooseIconDirectories =
        {
            @"C:\Users\sethm\Downloads\Elden Ring v1.14 Item Images and Maps\hi_01_common-tpf-dcx_split"
        };

        private static readonly string[] HighQualityItemArtDirectories =
        {
            @"C:\Users\sethm\Downloads\Elden Ring v1.14 Item Images and Maps\hi_00_solo-tpfbdt"
        };

        private static readonly string[] LoadoutRailSlots =
        {
            "Primary Right",
            "Primary Left",
            "Helmet",
            "Armor",
            "Gauntlet",
            "Leggings"
        };

        private static readonly string[] ConditionalLoadoutRailSlots =
        {
            "Accessory 1",
            "Accessory 2",
            "Accessory 3",
            "Accessory 4",
            "Accessory 5",
            "Talisman 1",
            "Talisman 2",
            "Talisman 3",
            "Talisman 4"
        };

        private static readonly string[] MagicRailSlots =
        {
            "Spell 1",
            "Spell 2",
            "Spell 3",
            "Spell 4",
            "Spell 5",
            "Spell 6",
            "Spell 7"
        };

        private static readonly Color AppBackground = Color.FromArgb(10, 10, 12);
        private static readonly Color Surface = Color.FromArgb(24, 24, 28);
        private static readonly Color SurfaceRaised = Color.FromArgb(36, 36, 41);
        private static readonly Color SurfaceGlass = Color.FromArgb(31, 31, 36);
        private static readonly Color RailDark = Color.FromArgb(17, 17, 20);
        private static readonly Color Hairline = Color.FromArgb(94, 255, 255, 255);
        private static readonly Color SoftHairline = Color.FromArgb(44, 255, 255, 255);
        private static readonly Color PrimaryText = Color.FromArgb(246, 246, 248);
        private static readonly Color SecondaryText = Color.FromArgb(184, 184, 190);
        private static readonly Color MutedText = Color.FromArgb(122, 122, 130);
        private static readonly object CharaInitDiagnosticsLock = new();
        private static readonly HashSet<string> CharaInitDiagnosticsKeys = new(StringComparer.Ordinal);
        private readonly PictureBox _portrait;
        private readonly Label _nameLabel;
        private readonly Label _nameQualifierLabel;
        private readonly Label _variantLabel;
        private readonly Label _paramLabel;
        private readonly Label _charaInitLabel;
        private readonly Label _hpLabel;
        private readonly Label _statusLabel;
        private readonly StatusOrbControl _statusOrb;
        private readonly Label _staggerLabel;
        private readonly Label _rewardsValueLabel;
        private readonly VitalBar _hpBar;
        private readonly VitalBar _staggerBar;
        private readonly FlowLayoutPanel _itemFlow;
        private readonly FlowLayoutPanel _spellFlow;
        private readonly Label _loadoutEmptyLabel;
        private readonly Label _magicEmptyLabel;
        private readonly Label _loadoutTitleLabel;
        private TableLayoutPanel? _rootLayout;
        private ColumnStyle? _itemHistoryColumnStyle;
        private ColumnStyle? _freecamDrawerColumnStyle;
        private RoundedPanel _itemHistoryPanel = null!;
        private RoundedPanel _freecamDrawerPanel = null!;
        private FlowLayoutPanel _itemHistoryFlow = null!;
        private Label _itemHistoryEmptyLabel = null!;
        private DrawerTabButton _itemHistoryToggle = null!;
        private DrawerTabButton _freecamDrawerToggle = null!;
        private Label _freecamStatusLabel = null!;
        private Label _freecamFieldAreaLabel = null!;
        private Label _freecamModeLabel = null!;
        private Control _freecamToggleButton = null!;
        private readonly System.Windows.Forms.Timer _itemHistoryIngestTimer;
        private ItemHoverOverlayForm? _itemHoverOverlay;
        private readonly System.Windows.Forms.Timer _hoverOverlayTimer;
        private Control? _activeHoverSource;
        private TableLayoutPanel? _stageLayout;
        private RowStyle? _loadoutRowStyle;
        private WindowChromeButton? _maximizeButton;
        private Rectangle _restoreBounds;
        private bool _isDraggingLoadoutGrip;
        private bool _isAdjustingDrawerBounds;
        private bool _isChromeMaximized;
        private int _loadoutDragStartY;
        private float _loadoutDragStartHeight;
        private bool _hasDisplayedStagger;
        private float _lastDisplayedStagger;

        private readonly CancellationTokenSource _shutdown = new();
        private readonly HashSet<int> _loggedParamIds;
        private readonly HashSet<string> _loggedEquipmentSignatures = new();
        private readonly HashSet<string> _loggedEquipmentDiagnostics = new();
        private readonly string _discoveredIdsPath;
        private readonly string _discoveredEquipmentPath;
        private readonly string _equipmentDiagnosticsPath;
        private readonly string _inventoryDiagnosticsPath;
        private readonly string _itemHistoryPath;
        private readonly string _itemPickupFeedPath;
        private readonly List<ItemHistoryEntry> _itemHistory = new();
        private readonly Dictionary<string, ItemInfo> _itemInfoCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<ItemInfo>> _loadoutCache = new(StringComparer.Ordinal);
        private readonly GameItemCatalog _itemCatalog;
        private readonly CharaInitLoadoutCatalog _charaInitLoadouts;
        private readonly EnemyCatalog _enemyCatalog;
        private readonly EnemyLoadoutCatalog _enemyLoadoutCatalog;
        private readonly EnemyLoadoutCatalog _enemyVisualLoadoutCatalog;
        private readonly EnemyMagicCatalog _enemyMagicCatalog;
        private readonly EnemyRewardCatalog _enemyRewardCatalog;
        private readonly RuneScalingCatalog _runeScalingCatalog;
        private EldenRingIconExtractor? _iconExtractor;

        private CharacterInfo? _lastTarget;
        private int _displayedLoadoutParamId = int.MinValue;
        private string _displayedLoadoutSignature = string.Empty;
        private int _lastDisplayedHp;
        private bool _hasEverShownTarget;
        private bool _targetIsActive;
        private bool _itemHistoryExpanded;
        private bool _freecamDrawerExpanded;
        private long _itemPickupFeedPosition;
        private DateTime _nextInventoryHistoryScanUtc = DateTime.MinValue;
        private string _lastInventoryHistorySignature = string.Empty;
        private string _lastInventoryDiagnostics = string.Empty;
        private string _lastStatusMessage = string.Empty;

        public MainForm()
        {
            Text = "EldenIntel";
            Width = 522;
            Height = 1214;
            MinimumSize = new Size(446, 760);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = AppBackground;
            ForeColor = PrimaryText;
            Font = new Font("Segoe UI", 10f);
            DoubleBuffered = true;
            _restoreBounds = Bounds;

            _discoveredIdsPath = Path.Combine(
                AppContext.BaseDirectory,
                "DiscoveredParamIds.txt"
            );
            _discoveredEquipmentPath = Path.Combine(
                AppContext.BaseDirectory,
                "DiscoveredLiveEquipment.txt"
            );
            _equipmentDiagnosticsPath = Path.Combine(
                AppContext.BaseDirectory,
                "EquipmentDiagnostics.txt"
            );
            _inventoryDiagnosticsPath = Path.Combine(
                AppContext.BaseDirectory,
                "InventoryDiagnostics.txt"
            );
            _itemHistoryPath = Path.Combine(
                AppContext.BaseDirectory,
                "RecentItemHistory.json"
            );
            _itemPickupFeedPath = Path.Combine(
                AppContext.BaseDirectory,
                "PickedUpItems.txt"
            );

            _loggedParamIds = LoadPreviouslyLoggedParamIds();
            _itemCatalog = GameItemCatalog.Load();
            _charaInitLoadouts = CharaInitLoadoutCatalog.Load();
            _enemyCatalog = EnemyCatalog.Load();
            _enemyLoadoutCatalog = EnemyLoadoutCatalog.Load();
            _enemyVisualLoadoutCatalog = EnemyLoadoutCatalog.Load("enemy_visual_loadouts.json");
            _enemyMagicCatalog = EnemyMagicCatalog.Load();
            _enemyRewardCatalog = EnemyRewardCatalog.Load();
            _runeScalingCatalog = RuneScalingCatalog.Load();
            _hoverOverlayTimer = new System.Windows.Forms.Timer
            {
                Interval = 80
            };
            _hoverOverlayTimer.Tick += (_, _) => CheckHoverOverlayDismiss();
            LoadItemHistory();
            if (File.Exists(_itemPickupFeedPath))
            {
                _itemPickupFeedPosition = new FileInfo(_itemPickupFeedPath).Length;
            }

            _itemHistoryIngestTimer = new System.Windows.Forms.Timer
            {
                Interval = 750
            };
            _itemHistoryIngestTimer.Tick += (_, _) => IngestPickedItemFeed();
            _statusOrb = new StatusOrbControl
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(8, 7, 0, 7),
                State = LinkState.Offline
            };

            var shell = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = AppBackground,
                Padding = new Padding(14, 10, 14, 12),
                ColumnCount = 1,
                RowCount = 2
            };
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            shell.Controls.Add(BuildHeader(), 0, 0);

            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = AppBackground,
                ColumnCount = 1,
                RowCount = 1,
                Margin = new Padding(0)
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _itemHistoryPanel = BuildItemHistoryPanel();
            _freecamDrawerPanel = BuildFreecamDrawerPanel();

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = AppBackground,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ItemHistoryDrawerCollapsedWidth));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, FreecamDrawerCollapsedWidth));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _rootLayout = root;
            _itemHistoryColumnStyle = root.ColumnStyles[0];
            _freecamDrawerColumnStyle = root.ColumnStyles[2];
            SetItemHistoryExpanded(false);
            SetFreecamDrawerExpanded(false);

            var identityCard = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = SurfaceGlass,
                BorderColor = Hairline,
                BorderThickness = 1,
                CornerRadius = 24,
                Padding = new Padding(18, 11, 18, 8),
                Margin = new Padding(0, 0, 0, 9)
            };

            var identityLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 5
            };
            identityLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            identityLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
            identityLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));
            identityLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
            identityLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            identityLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));

            _nameLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "WAITING FOR ELDEN RING",
                Font = new Font("Segoe UI Semibold", 17f),
                ForeColor = PrimaryText,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _nameQualifierLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = string.Empty,
                Font = new Font("Segoe UI Semibold", 7.5f),
                ForeColor = SecondaryText,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.TopLeft
            };

            _variantLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "No target acquired",
                Font = new Font("Segoe UI", 9.75f),
                ForeColor = SecondaryText,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _paramLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "PARAM ID  —",
                Font = new Font("Consolas", 9f),
                ForeColor = MutedText,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _charaInitLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "CHARAINIT  —",
                Font = new Font("Consolas", 9f),
                ForeColor = MutedText,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };

            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Waiting for eldenring.exe...",
                ForeColor = MutedText,
                Font = new Font("Segoe UI", 9f),
                TextAlign = ContentAlignment.BottomLeft,
                AutoEllipsis = true
            };

            identityLayout.Controls.Add(_nameLabel, 0, 0);
            identityLayout.Controls.Add(_nameQualifierLabel, 0, 1);
            identityLayout.Controls.Add(_variantLabel, 0, 2);
            identityLayout.Controls.Add(_paramLabel, 0, 3);
            identityLayout.Controls.Add(_charaInitLabel, 0, 4);
            identityCard.Controls.Add(identityLayout);

            var vitalCard = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = SurfaceGlass,
                BorderColor = Hairline,
                BorderThickness = 1,
                CornerRadius = 22,
                Padding = new Padding(18, 10, 18, 10),
                Margin = new Padding(0)
            };

            var vitalLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 2
            };
            vitalLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            vitalLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            _hpLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "HEALTH  —",
                Font = new Font("Segoe UI Semibold", 9.5f),
                ForeColor = SecondaryText,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _hpBar = new VitalBar
            {
                Dock = DockStyle.Fill,
                Maximum = 100,
                Value = 0,
                TrackColor = Color.FromArgb(38, 38, 43),
                FillColor = Color.FromArgb(58, 206, 118),
                WarningFillColor = Color.FromArgb(232, 196, 67),
                CriticalFillColor = Color.FromArgb(229, 74, 74),
                CornerRadius = 9,
                ShowValueText = true,
                Text = "HP  —"
            };

            _staggerLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "STAGGER  AWAITING MEMORY LINK",
                Font = new Font("Segoe UI Semibold", 9f),
                ForeColor = MutedText,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _staggerBar = new VitalBar
            {
                Dock = DockStyle.Fill,
                Maximum = 100,
                Value = 0,
                TrackColor = Color.FromArgb(34, 34, 39),
                FillColor = Color.FromArgb(58, 206, 118),
                WarningFillColor = Color.FromArgb(232, 196, 67),
                CriticalFillColor = Color.FromArgb(229, 74, 74),
                CornerRadius = 7,
                ShowValueText = true,
                Text = "STAGGER  —"
            };

            vitalLayout.Controls.Add(_hpBar, 0, 0);
            vitalLayout.Controls.Add(_staggerBar, 0, 1);
            vitalCard.Controls.Add(vitalLayout);

            var intelCard = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(22, 22, 26),
                BorderColor = Hairline,
                BorderThickness = 1,
                CornerRadius = 18,
                Padding = new Padding(16),
                Margin = new Padding(0)
            };

            _rewardsValueLabel = BuildMetricValueLabel("Runes resolve by Param ID");
            _rewardsValueLabel.AutoEllipsis = false;
            _rewardsValueLabel.Font = new Font("Segoe UI", 8.25f);

            var intelLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 5
            };
            intelLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            intelLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            intelLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            intelLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            intelLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            intelLayout.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "CHARACTER SHEET",
                ForeColor = PrimaryText,
                Font = new Font("Segoe UI Semibold", 11f),
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);

            intelLayout.Controls.Add(BuildMetricBlock("LOCK SOURCE", "Live lock-on pointer"), 0, 1);
            intelLayout.Controls.Add(BuildMetricBlock("EQUIPMENT", "Verified Param ID loadout"), 0, 2);
            intelLayout.Controls.Add(BuildMetricBlock("REWARDS", _rewardsValueLabel), 0, 3);
            intelLayout.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "Equipment slots frame the locked enemy. Double-click any card or slot to open its item sheet.",
                ForeColor = MutedText,
                Font = new Font("Segoe UI", 9f),
                TextAlign = ContentAlignment.TopLeft
            }, 0, 4);
            intelCard.Controls.Add(intelLayout);

            var portraitPanel = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(14, 14, 17),
                BorderColor = Hairline,
                BorderThickness = 1,
                CornerRadius = 24,
                Padding = new Padding(8),
                Margin = new Padding(0, 0, 0, 0)
            };

            var portraitLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 3,
                RowCount = 1
            };
            portraitLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            portraitLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            portraitLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            portraitLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var itemHost = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = RailDark,
                BorderColor = SoftHairline,
                BorderThickness = 1,
                CornerRadius = 16,
                Padding = new Padding(7),
                Margin = new Padding(0, 0, 8, 0)
            };

            _itemFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = false,
                Padding = new Padding(0)
            };

            _loadoutEmptyLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = string.Empty,
                ForeColor = MutedText,
                Font = new Font("Consolas", 7.5f),
                TextAlign = ContentAlignment.MiddleCenter
            };

            itemHost.Controls.Add(_itemFlow);
            itemHost.Controls.Add(_loadoutEmptyLabel);
            _loadoutEmptyLabel.BringToFront();

            var magicHost = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = RailDark,
                BorderColor = SoftHairline,
                BorderThickness = 1,
                CornerRadius = 16,
                Padding = new Padding(7),
                Margin = new Padding(8, 0, 0, 0)
            };

            _spellFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = false,
                Padding = new Padding(0)
            };

            _magicEmptyLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = string.Empty,
                ForeColor = MutedText,
                Font = new Font("Consolas", 7.5f),
                TextAlign = ContentAlignment.MiddleCenter
            };

            magicHost.Controls.Add(_spellFlow);
            magicHost.Controls.Add(_magicEmptyLabel);
            _magicEmptyLabel.BringToFront();

            _portrait = new VerticalFitPictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(12, 12, 14)
            };
            portraitLayout.Controls.Add(itemHost, 0, 0);
            portraitLayout.Controls.Add(_portrait, 1, 0);
            portraitLayout.Controls.Add(magicHost, 2, 0);
            portraitPanel.Controls.Add(portraitLayout);

            var characterStage = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(18, 18, 22),
                BorderColor = Color.FromArgb(112, 255, 255, 255),
                BorderThickness = 1,
                CornerRadius = 28,
                Padding = new Padding(10),
                Margin = new Padding(0, 0, 10, 0)
            };

            var stageLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 4
            };
            stageLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            stageLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 226));
            stageLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            stageLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 7));
            stageLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
            _stageLayout = stageLayout;
            _loadoutRowStyle = stageLayout.RowStyles[3];

            var topStatus = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0, 0, 0, 12)
            };
            topStatus.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            topStatus.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
            topStatus.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            topStatus.Controls.Add(identityCard, 0, 0);
            topStatus.Controls.Add(vitalCard, 0, 1);

            stageLayout.Controls.Add(topStatus, 0, 0);
            stageLayout.Controls.Add(portraitPanel, 0, 1);
            stageLayout.Controls.Add(BuildLoadoutResizeGrip(), 0, 2);
            characterStage.Controls.Add(stageLayout);
            content.Controls.Add(characterStage, 0, 0);

            var loadoutCard = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = SurfaceGlass,
                BorderColor = Hairline,
                BorderThickness = 1,
                CornerRadius = 24,
                Padding = new Padding(14, 12, 14, 12),
                Margin = new Padding(0)
            };

            var loadoutLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 2
            };
            loadoutLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            loadoutLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var loadoutHeader = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 2,
                RowCount = 1
            };
            loadoutHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            loadoutHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));

            _loadoutTitleLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "REWARDS",
                ForeColor = PrimaryText,
                Font = new Font("Segoe UI Semibold", 12f),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var hintLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = string.Empty,
                ForeColor = MutedText,
                Font = new Font("Consolas", 7.5f),
                TextAlign = ContentAlignment.MiddleRight
            };

            loadoutHeader.Controls.Add(_loadoutTitleLabel, 0, 0);
            loadoutHeader.Controls.Add(hintLabel, 1, 0);

            loadoutLayout.Controls.Add(loadoutHeader, 0, 0);
            loadoutLayout.Controls.Add(BuildMetricBlock("REWARDS", _rewardsValueLabel), 0, 1);
            loadoutCard.Controls.Add(loadoutLayout);
            stageLayout.Controls.Add(loadoutCard, 0, 3);

            shell.Controls.Add(content, 0, 1);
            root.Controls.Add(_itemHistoryPanel, 0, 0);
            root.Controls.Add(shell, 1, 0);
            root.Controls.Add(_freecamDrawerPanel, 2, 0);
            Controls.Add(root);

            RenderLoadoutRail(Array.Empty<ItemInfo>());
            RenderMagicRail(Array.Empty<ItemInfo>());
            RenderItemHistory();
            InitializeIconExtractor();

            Resize += (_, _) =>
            {
                if (_isAdjustingDrawerBounds)
                {
                    return;
                }

                ResizeItemCards();
                UpdateItemHistoryPanelMargin();
                UpdateFreecamDrawerPanelMargin();
            };
            FormClosing += (_, _) =>
            {
                _shutdown.Cancel();
                _hoverOverlayTimer.Stop();
                _itemHistoryIngestTimer.Stop();
                _hoverOverlayTimer.Dispose();
                _itemHistoryIngestTimer.Dispose();
                DisposePortrait();
                DisposeItemCards();
                DisposeItemHistoryCards();
                _itemHoverOverlay?.Dispose();
                _itemHoverOverlay = null;
                _iconExtractor?.Dispose();
                _iconExtractor = null;
                MainFormImageLoader.Clear();
            };

            Shown += (_, _) =>
            {
                _restoreBounds = Bounds;
                _itemHistoryIngestTimer.Start();
                Thread readerThread = new Thread(ReaderLoop)
                {
                    IsBackground = true,
                    Name = "EnemyIntelReader"
                };

                readerThread.Start();
            };
        }

        private void InitializeIconExtractor()
        {
            try
            {
                _iconExtractor = new EldenRingIconExtractor(
                    IconLayoutDirectory,
                    IconTextureDirectory
                );

                _statusLabel.Text =
                    $"Icon atlas ready — {_iconExtractor.IndexedIconCount:N0} icons indexed.";
            }
            catch (Exception exception)
            {
                _iconExtractor = null;
                _statusLabel.Text =
                    $"Icon atlas unavailable: {exception.Message}";
            }
        }

        private RoundedPanel BuildItemHistoryPanel()
        {
            var panel = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = RailDark,
                BorderColor = SoftHairline,
                BorderThickness = 1,
                CornerRadius = 24,
                Padding = new Padding(7),
                Margin = new Padding(0, 62, 0, 14)
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 3
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _itemHistoryToggle = new DrawerTabButton
            {
                Dock = DockStyle.Fill,
                Text = "ITEMS",
                BackColor = SurfaceRaised,
                ForeColor = PrimaryText,
                Font = new Font("Consolas", 8f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            _itemHistoryToggle.Click += (_, _) => ToggleItemHistory();

            var title = new Label
            {
                Dock = DockStyle.Fill,
                Text = "RECENT / 20",
                ForeColor = PrimaryText,
                Font = new Font("Segoe UI Semibold", 10f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 0, 0)
            };

            var historyHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent
            };

            _itemHistoryFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = false,
                Padding = new Padding(0)
            };

            _itemHistoryEmptyLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "NO ITEMS",
                ForeColor = MutedText,
                Font = new Font("Consolas", 7.5f),
                TextAlign = ContentAlignment.MiddleCenter
            };

            historyHost.Controls.Add(_itemHistoryFlow);
            historyHost.Controls.Add(_itemHistoryEmptyLabel);
            _itemHistoryEmptyLabel.BringToFront();

            layout.Controls.Add(_itemHistoryToggle, 0, 0);
            layout.Controls.Add(title, 0, 1);
            layout.Controls.Add(historyHost, 0, 2);
            panel.Controls.Add(layout);

            return panel;
        }

        private void ToggleItemHistory()
        {
            SetItemHistoryExpanded(!_itemHistoryExpanded);
        }

        private void SetItemHistoryExpanded(bool expanded)
        {
            int oldWidth = _itemHistoryExpanded
                ? ItemHistoryDrawerExpandedWidth
                : ItemHistoryDrawerCollapsedWidth;
            int newWidth = expanded
                ? ItemHistoryDrawerExpandedWidth
                : ItemHistoryDrawerCollapsedWidth;
            bool shouldAdjustBounds =
                IsHandleCreated &&
                Visible &&
                WindowState == FormWindowState.Normal &&
                oldWidth != newWidth;

            _itemHistoryExpanded = expanded;

            _itemHistoryToggle.Text = expanded ? "HIDE" : "ITEMS";
            _itemHistoryToggle.Expanded = expanded;
            if (_itemHistoryColumnStyle != null)
            {
                _itemHistoryColumnStyle.Width = newWidth;
            }

            _itemHistoryPanel.CornerRadius = expanded ? 24 : 18;
            _itemHistoryPanel.Padding = expanded
                ? new Padding(7)
                : new Padding(0);
            UpdateItemHistoryPanelMargin();

            foreach (Control control in _itemHistoryPanel.Controls)
            {
                if (control is TableLayoutPanel layout && layout.Controls.Count >= 2)
                {
                    layout.Controls[1].Visible = expanded;
                    layout.Controls[2].Visible = expanded;
                    layout.RowStyles[0].SizeType = expanded
                        ? SizeType.Absolute
                        : SizeType.Percent;
                    layout.RowStyles[0].Height = expanded ? 34 : 100;
                    layout.RowStyles[1].SizeType = expanded
                        ? SizeType.Absolute
                        : SizeType.Absolute;
                    layout.RowStyles[1].Height = expanded ? 34 : 0;
                    layout.RowStyles[2].SizeType = expanded
                        ? SizeType.Percent
                        : SizeType.Absolute;
                    layout.RowStyles[2].Height = expanded ? 100 : 0;
                }
            }

            if (shouldAdjustBounds)
            {
                int delta = newWidth - oldWidth;
                _isAdjustingDrawerBounds = true;
                try
                {
                    SetBounds(Left - delta, Top, Width + delta, Height);
                }
                finally
                {
                    _isAdjustingDrawerBounds = false;
                }
            }

            _rootLayout?.PerformLayout();
        }

        private RoundedPanel BuildFreecamDrawerPanel()
        {
            var panel = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = RailDark,
                BorderColor = SoftHairline,
                BorderThickness = 1,
                CornerRadius = 24,
                Padding = new Padding(7),
                Margin = new Padding(0, 62, 0, 14)
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 10,
                Margin = new Padding(0)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _freecamDrawerToggle = new DrawerTabButton
            {
                Dock = DockStyle.Fill,
                Text = "CAM",
                Margin = new Padding(0, 0, 0, 4)
            };
            _freecamDrawerToggle.Click += (_, _) => ToggleFreecamDrawer();

            var title = new Label
            {
                Dock = DockStyle.Fill,
                Text = "FREECAM",
                ForeColor = PrimaryText,
                Font = new Font("Segoe UI Semibold", 12f),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };

            _freecamStatusLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "STANDBY",
                ForeColor = SecondaryText,
                Font = new Font("Consolas", 8.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };

            var fieldCaption = BuildFreecamCaption("FIELD");
            _freecamFieldAreaLabel = BuildFreecamValue("FIELD AREA --");
            var stateCaption = BuildFreecamCaption("STATE");
            _freecamModeLabel = BuildFreecamValue("MODE DISABLED");

            _freecamToggleButton = BuildFreecamActionButton("TOGGLE");
            _freecamToggleButton.Click += (_, _) =>
            {
                _freecamStatusLabel.Text = "MEMORY WIRING PENDING";
            };

            var resetButton = BuildFreecamActionButton("RESET VIEW");
            resetButton.Click += (_, _) =>
            {
                _freecamStatusLabel.Text = "RESET WIRING PENDING";
            };

            layout.Controls.Add(_freecamDrawerToggle, 0, 0);
            layout.Controls.Add(title, 0, 1);
            layout.Controls.Add(_freecamStatusLabel, 0, 2);
            layout.Controls.Add(fieldCaption, 0, 3);
            layout.Controls.Add(_freecamFieldAreaLabel, 0, 4);
            layout.Controls.Add(stateCaption, 0, 5);
            layout.Controls.Add(_freecamModeLabel, 0, 6);
            layout.Controls.Add(_freecamToggleButton, 0, 7);
            layout.Controls.Add(resetButton, 0, 8);
            panel.Controls.Add(layout);

            return panel;
        }

        private static Label BuildFreecamCaption(string text)
        {
            return new Label
            {
                Dock = DockStyle.Fill,
                Text = text,
                ForeColor = MutedText,
                Font = new Font("Consolas", 7.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.BottomLeft,
                AutoEllipsis = true,
                Margin = new Padding(0)
            };
        }

        private static Label BuildFreecamValue(string text)
        {
            return new Label
            {
                Dock = DockStyle.Fill,
                Text = text,
                ForeColor = SecondaryText,
                Font = new Font("Consolas", 8.25f),
                TextAlign = ContentAlignment.TopLeft,
                AutoEllipsis = true,
                Margin = new Padding(0)
            };
        }

        private Control BuildFreecamActionButton(string text)
        {
            var button = new FreecamActionButton(text)
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 4, 0, 0)
            };
            return button;
        }

        private void ToggleFreecamDrawer()
        {
            SetFreecamDrawerExpanded(!_freecamDrawerExpanded);
        }

        private void SetFreecamDrawerExpanded(bool expanded)
        {
            int oldWidth = _freecamDrawerExpanded
                ? FreecamDrawerExpandedWidth
                : FreecamDrawerCollapsedWidth;
            int newWidth = expanded
                ? FreecamDrawerExpandedWidth
                : FreecamDrawerCollapsedWidth;
            bool shouldAdjustBounds =
                IsHandleCreated &&
                Visible &&
                WindowState == FormWindowState.Normal &&
                oldWidth != newWidth;

            _freecamDrawerExpanded = expanded;

            _freecamDrawerToggle.Text = "CAM";
            _freecamDrawerToggle.Expanded = expanded;
            if (_freecamDrawerColumnStyle != null)
            {
                _freecamDrawerColumnStyle.Width = newWidth;
            }

            _freecamDrawerPanel.CornerRadius = expanded ? 24 : 18;
            _freecamDrawerPanel.Padding = expanded
                ? new Padding(7)
                : new Padding(0);
            UpdateFreecamDrawerPanelMargin();

            foreach (Control control in _freecamDrawerPanel.Controls)
            {
                if (control is TableLayoutPanel layout && layout.Controls.Count >= 9)
                {
                    for (int index = 1; index < layout.Controls.Count; index++)
                    {
                        layout.Controls[index].Visible = expanded;
                    }

                    layout.RowStyles[0].SizeType = expanded
                        ? SizeType.Absolute
                        : SizeType.Percent;
                    layout.RowStyles[0].Height = expanded ? 32 : 100;

                    for (int index = 1; index < layout.RowStyles.Count - 1; index++)
                    {
                        layout.RowStyles[index].SizeType = SizeType.Absolute;
                    }

                    layout.RowStyles[1].Height = expanded ? 34 : 0;
                    layout.RowStyles[2].Height = expanded ? 30 : 0;
                    layout.RowStyles[3].Height = expanded ? 20 : 0;
                    layout.RowStyles[4].Height = expanded ? 24 : 0;
                    layout.RowStyles[5].Height = expanded ? 20 : 0;
                    layout.RowStyles[6].Height = expanded ? 24 : 0;
                    layout.RowStyles[7].Height = expanded ? 40 : 0;
                    layout.RowStyles[8].Height = expanded ? 40 : 0;
                    layout.RowStyles[9].SizeType = expanded
                        ? SizeType.Percent
                        : SizeType.Absolute;
                    layout.RowStyles[9].Height = expanded ? 100 : 0;
                }
            }

            if (shouldAdjustBounds)
            {
                int delta = newWidth - oldWidth;
                _isAdjustingDrawerBounds = true;
                try
                {
                    SetBounds(Left, Top, Width + delta, Height);
                }
                finally
                {
                    _isAdjustingDrawerBounds = false;
                }
            }

            _rootLayout?.PerformLayout();
        }

        private void UpdateItemHistoryPanelMargin()
        {
            if (_itemHistoryPanel == null)
            {
                return;
            }

            _itemHistoryPanel.Margin = _itemHistoryExpanded
                ? new Padding(0, 62, 10, 14)
                : new Padding(0, 74, 0, Math.Max(14, ClientSize.Height - 74 - 136));
        }

        private void UpdateFreecamDrawerPanelMargin()
        {
            if (_freecamDrawerPanel == null)
            {
                return;
            }

            _freecamDrawerPanel.Margin = _freecamDrawerExpanded
                ? new Padding(10, 62, 0, 14)
                : new Padding(0, 74, 0, Math.Max(14, ClientSize.Height - 74 - 136));
        }

        private Control BuildHeader()
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = AppBackground,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 214));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));

            var titleBlock = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 1
            };
            titleBlock.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            titleBlock.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "ENEMY INTEL",
                ForeColor = PrimaryText,
                Font = new Font("Segoe UI Semibold", 16.5f),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);

            var version = BuildPill("V2  •  LOCAL");
            version.Dock = DockStyle.Fill;
            version.Margin = new Padding(14, 6, 7, 6);

            var statusBlock = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 2,
                RowCount = 1
            };
            statusBlock.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            statusBlock.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
            statusBlock.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            statusBlock.Controls.Add(version, 0, 0);
            statusBlock.Controls.Add(_statusOrb, 1, 0);

            var chromeButtons = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(4, 6, 0, 6)
            };
            chromeButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            chromeButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            chromeButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
            chromeButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var minimizeButton = new WindowChromeButton("-")
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 5, 0)
            };
            minimizeButton.Click += (_, _) => WindowState = FormWindowState.Minimized;

            _maximizeButton = new WindowChromeButton("□")
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 5, 0)
            };
            _maximizeButton.Click += (_, _) => ToggleWindowMaximize();

            var closeButton = new WindowChromeButton("×", destructive: true)
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0)
            };
            closeButton.Click += (_, _) => Close();

            chromeButtons.Controls.Add(minimizeButton, 0, 0);
            chromeButtons.Controls.Add(_maximizeButton, 1, 0);
            chromeButtons.Controls.Add(closeButton, 2, 0);

            header.Controls.Add(titleBlock, 0, 0);
            header.Controls.Add(statusBlock, 1, 0);
            header.Controls.Add(chromeButtons, 2, 0);
            WireWindowDrag(header);
            WireWindowDrag(titleBlock);
            return header;
        }

        private void WireWindowDrag(Control control)
        {
            if (control is WindowChromeButton or DrawerTabButton or StatusOrbControl)
            {
                return;
            }

            control.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left && e.Clicks == 1)
                {
                    BeginWindowDrag();
                }
            };
            control.DoubleClick += (_, _) => ToggleWindowMaximize();

            foreach (Control child in control.Controls)
            {
                WireWindowDrag(child);
            }
        }

        private void BeginWindowDrag()
        {
            if (_isChromeMaximized)
            {
                Point cursor = Cursor.Position;
                float ratio = ClientSize.Width <= 0
                    ? 0.5f
                    : Math.Clamp((float)(cursor.X - Left) / ClientSize.Width, 0.05f, 0.95f);
                ToggleWindowMaximize();
                Left = cursor.X - (int)Math.Round(Width * ratio);
                Top = cursor.Y - 24;
            }

            ReleaseCapture();
            SendMessage(Handle, 0x00A1, HtCaption, 0);
        }

        private void ToggleWindowMaximize()
        {
            if (_isChromeMaximized)
            {
                _isChromeMaximized = false;
                Bounds = _restoreBounds;
                _maximizeButton?.SetGlyph("□");
                return;
            }

            _restoreBounds = Bounds;
            Rectangle workingArea = Screen.FromHandle(Handle).WorkingArea;
            _isChromeMaximized = true;
            Bounds = workingArea;
            _maximizeButton?.SetGlyph("❐");
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= CsDropShadow;
                return parameters;
            }
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (!_isChromeMaximized && WindowState == FormWindowState.Normal && !_isAdjustingDrawerBounds)
            {
                _restoreBounds = Bounds;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!_isChromeMaximized && WindowState == FormWindowState.Normal && !_isAdjustingDrawerBounds)
            {
                _restoreBounds = Bounds;
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg != WmNcHitTest || m.Result != new IntPtr(HtClient) || _isChromeMaximized)
            {
                return;
            }

            Point cursor = PointToClient(Cursor.Position);
            bool left = cursor.X <= ResizeHitSize;
            bool right = cursor.X >= ClientSize.Width - ResizeHitSize;
            bool top = cursor.Y <= ResizeHitSize;
            bool bottom = cursor.Y >= ClientSize.Height - ResizeHitSize;

            if (left && top)
            {
                m.Result = new IntPtr(HtTopLeft);
            }
            else if (right && top)
            {
                m.Result = new IntPtr(HtTopRight);
            }
            else if (left && bottom)
            {
                m.Result = new IntPtr(HtBottomLeft);
            }
            else if (right && bottom)
            {
                m.Result = new IntPtr(HtBottomRight);
            }
            else if (left)
            {
                m.Result = new IntPtr(HtLeft);
            }
            else if (right)
            {
                m.Result = new IntPtr(HtRight);
            }
            else if (top)
            {
                m.Result = new IntPtr(HtTop);
            }
            else if (bottom)
            {
                m.Result = new IntPtr(HtBottom);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        private static Label BuildPill(string text)
        {
            return new Label
            {
                Text = text,
                BackColor = SurfaceGlass,
                ForeColor = SecondaryText,
                Font = new Font("Consolas", 9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(8, 0, 8, 0)
            };
        }

        private static Control BuildMetricBlock(string label, string value)
        {
            return BuildMetricBlock(label, BuildMetricValueLabel(value));
        }

        private static Label BuildMetricValueLabel(string value)
        {
            return new Label
            {
                Dock = DockStyle.Fill,
                Text = value,
                ForeColor = SecondaryText,
                Font = new Font("Segoe UI Semibold", 9.5f),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.TopLeft
            };
        }

        private static Control BuildMetricBlock(string label, Label valueLabel)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0, 0, 0, 8)
            };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            panel.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = label,
                ForeColor = MutedText,
                Font = new Font("Consolas", 8f, FontStyle.Bold),
                TextAlign = ContentAlignment.BottomLeft
            }, 0, 0);

            panel.Controls.Add(valueLabel, 0, 1);

            return panel;
        }

        private Control BuildLoadoutResizeGrip()
        {
            var grip = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Cursor = Cursors.SizeNS,
                Margin = new Padding(0)
            };

            grip.Paint += (_, e) =>
            {
                int y = grip.Height / 2;
                using Pen line = new(Color.FromArgb(65, 255, 255, 255), 1f);
                e.Graphics.DrawLine(line, 22, y, Math.Max(22, grip.Width - 22), y);
            };

            grip.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left || _loadoutRowStyle == null)
                {
                    return;
                }

                _isDraggingLoadoutGrip = true;
                _loadoutDragStartY = Control.MousePosition.Y;
                _loadoutDragStartHeight = _loadoutRowStyle.Height;
                grip.Capture = true;
            };

            grip.MouseMove += (_, _) =>
            {
                if (!_isDraggingLoadoutGrip || _loadoutRowStyle == null)
                {
                    return;
                }

                int delta = Control.MousePosition.Y - _loadoutDragStartY;
                int maxHeight = _stageLayout == null
                    ? 520
                    : Math.Max(140, _stageLayout.Height - 310);
                float height = Math.Clamp(_loadoutDragStartHeight - delta, 118, maxHeight);
                _loadoutRowStyle.Height = height;
                _stageLayout?.PerformLayout();
            };

            grip.MouseUp += (_, _) =>
            {
                _isDraggingLoadoutGrip = false;
                grip.Capture = false;
            };

            return grip;
        }

        private void ReaderLoop()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                if (!IsEldenRingRunning())
                {
                    SetInactiveState("Waiting for eldenring.exe...", "OFFLINE");
                    Thread.Sleep(1000);
                    continue;
                }

                try
                {
                    UpdateStatus("Elden Ring detected. Connecting...", LinkState.Linking);

                    using MemoryReader memory = new MemoryReader(ProcessName);
                    ulong lockOnAccessor = ResolveLockOnAccessor(memory);
                    ulong worldChrMan = ResolveGlobalPointerOrZero(memory, WorldChrManPattern);
                    ulong gameDataMan = ResolveGlobalPointerOrZero(memory, GameDataManPattern);

                    using LockOnHook hook = LockOnHook.Install(memory, lockOnAccessor);
                    UpdateStatus("Reader active. Lock onto an enemy.", LinkState.Linking);
                    MonitorTargets(memory, hook, worldChrMan, gameDataMan);
                }
                catch (Exception exception)
                {
                    if (_shutdown.IsCancellationRequested)
                    {
                        return;
                    }

                    SetInactiveState($"Reader paused: {exception.Message}", "PAUSED");
                    Thread.Sleep(1500);
                }
            }
        }

        internal static ulong ResolveLockOnAccessor(MemoryReader memory)
        {
            try
            {
                return memory.ScanModule(LockOnPattern);
            }
            catch (InvalidOperationException normalScanException)
            {
                try
                {
                    return memory.ScanModule(PatchedLockOnPattern);
                }
                catch
                {
                    throw normalScanException;
                }
            }
        }

        internal static ulong ResolveGlobalPointerOrZero(MemoryReader memory, string pattern)
        {
            try
            {
                ulong instruction = memory.ScanModule(pattern);
                int displacement = memory.ReadInt32(instruction + 3);
                ulong pointerAddress = unchecked((ulong)((long)instruction + 7 + displacement));
                ulong pointer = memory.ReadUInt64(pointerAddress);
                return memory.IsLikelyPointer(pointer) ? pointer : 0;
            }
            catch
            {
                return 0;
            }
        }

        internal static int ReadClearCount(MemoryReader memory, ulong gameDataMan)
        {
            if (!memory.IsLikelyPointer(gameDataMan))
            {
                return -1;
            }

            try
            {
                int clearCount = memory.ReadInt32(gameDataMan + 0x120);
                return clearCount is >= 0 and <= 8 ? clearCount : -1;
            }
            catch
            {
                return -1;
            }
        }

        private void MonitorTargets(MemoryReader memory, LockOnHook hook, ulong worldChrMan, ulong gameDataMan)
        {
            while (!_shutdown.IsCancellationRequested)
            {
                if (memory.Process.HasExited)
                {
                    SetInactiveState("Elden Ring closed — showing last target.", "OFFLINE");
                    return;
                }

                try
                {
                    UpdateItemHistoryFromInventory(memory, gameDataMan);

                    ulong characterAddress = memory.ReadUInt64(hook.TargetStorageAddress);
                    ulong lockOnManager = memory.ReadUInt64(hook.ManagerStorageAddress);
                    TargetHandleInfo targetHandle = ReadPlayerTargetHandle(memory, worldChrMan);
                    ulong managerHandle = targetHandle.PackedHandle;

                    if (managerHandle == 0 && memory.IsLikelyPointer(lockOnManager))
                    {
                        try
                        {
                            managerHandle = memory.ReadUInt64(lockOnManager + 0x6B0);
                            targetHandle = new TargetHandleInfo(
                                managerHandle,
                                unchecked((uint)(managerHandle & 0xFFFFFFFF)),
                                unchecked((int)(managerHandle >> 32)),
                                "lock-on manager");
                        }
                        catch
                        {
                            // Captured character can remain valid while the manager pointer is briefly unreadable.
                        }
                    }

                    if (!memory.IsLikelyPointer(characterAddress) && managerHandle != 0)
                    {
                        characterAddress = FindWorldChrManCharacter(
                            memory,
                            worldChrMan,
                            targetHandle.LocalId,
                            targetHandle.Area);
                    }

                    if (!memory.IsLikelyPointer(characterAddress))
                    {
                        SetInactiveState("No active target — showing last enemy.", "LAST TARGET");
                        Thread.Sleep(100);
                        continue;
                    }

                    ulong packedHandle = managerHandle != 0
                        ? managerHandle
                        : memory.ReadUInt64(characterAddress + 0x08);

                    uint targetLocalId = targetHandle.LocalId != 0
                        ? targetHandle.LocalId
                        : (uint)(packedHandle & 0xFFFFFFFF);
                    CharacterInfo target = ReadCharacter(
                        memory,
                        characterAddress,
                        worldChrMan,
                        _itemCatalog,
                        _charaInitLoadouts,
                        _enemyLoadoutCatalog,
                        _enemyVisualLoadoutCatalog);
                    target.ClearCount = ReadClearCount(memory, gameDataMan);

                    if (targetLocalId != 0 && targetLocalId != target.LocalId)
                    {
                        ulong resolvedAddress = FindWorldChrManCharacter(memory, worldChrMan, targetLocalId, targetHandle.Area);
                        if (memory.IsLikelyPointer(resolvedAddress))
                        {
                            target = ReadCharacter(
                                memory,
                                resolvedAddress,
                                worldChrMan,
                                _itemCatalog,
                                _charaInitLoadouts,
                                _enemyLoadoutCatalog,
                                _enemyVisualLoadoutCatalog);
                            target.ClearCount = ReadClearCount(memory, gameDataMan);
                        }
                    }

                    if (!HasReadableTargetVitals(target))
                    {
                        SetInactiveState("No active target — showing last enemy.", "LAST TARGET");
                        Thread.Sleep(75);
                        continue;
                    }

                    LogParamIdIfNew(target.ParamId);
                    LogEquipmentIfNew(target);
                    LogEquipmentDiagnosticsIfNeeded(memory, target, worldChrMan);

                    bool changed =
                        _lastTarget == null ||
                        _lastTarget.Address != target.Address ||
                        _lastTarget.ParamId != target.ParamId ||
                        _lastTarget.CharaInitParamId != target.CharaInitParamId ||
                        _lastTarget.ClearCount != target.ClearCount ||
                        _lastTarget.CurrentHp != target.CurrentHp ||
                        _lastTarget.MaxHp != target.MaxHp ||
                        !StaggerEquals(_lastTarget.Stagger, target.Stagger) ||
                        !_targetIsActive;

                    if (changed)
                    {
                        _lastTarget = target;
                        _targetIsActive = true;
                        ShowTarget(target);
                    }
                }
                catch (MemoryReadException)
                {
                    SetInactiveState("Target read interrupted — showing last enemy.", "LAST TARGET");
                }

                Thread.Sleep(50);
            }
        }

        internal static CharacterInfo ReadCharacter(
            MemoryReader memory,
            ulong characterAddress,
            ulong worldChrMan,
            GameItemCatalog itemCatalog,
            CharaInitLoadoutCatalog charaInitLoadouts,
            EnemyLoadoutCatalog? enemyLoadouts = null,
            EnemyLoadoutCatalog? enemyVisualLoadouts = null)
        {
            CharacterInfo character = new CharacterInfo
            {
                Address = characterAddress,
                LocalId = memory.ReadUInt32(characterAddress + 0x08),
                ParamId = memory.ReadInt32(characterAddress + 0x60),
                GlobalId = memory.ReadUInt16(characterAddress + 0x74)
            };

            ulong characterData = memory.ReadUInt64(characterAddress + 0x190);
            if (!memory.IsLikelyPointer(characterData))
            {
                return character;
            }

            ulong hpObject = memory.ReadUInt64(characterData + 0x00);
            if (!memory.IsLikelyPointer(hpObject))
            {
                return character;
            }

            ulong packedHealth = memory.ReadUInt64(hpObject + 0x138);
            character.CurrentHp = unchecked((int)(packedHealth & 0xFFFFFFFF));
            character.MaxHp = unchecked((int)(packedHealth >> 32));

            character.Stagger = ReadStaggerProbe(memory, characterData);
            character.Equipment = ReadCharacterEquipmentProbe(memory, character, worldChrMan, characterData, itemCatalog);
            CharaInitProbeResult charaInit = ReadCharaInitParamProbe(
                memory,
                characterAddress,
                characterData,
                character.ParamId,
                character.Equipment,
                BuildExpectedEquipmentProbes(character.ParamId, enemyLoadouts, enemyVisualLoadouts),
                charaInitLoadouts);
            character.CharaInitParamId = charaInit.ParamId;
            character.CharaInitParamSource = charaInit.Source;
            character.CharaInitCandidateParamId = charaInit.CandidateParamId;
            character.CharaInitCandidateSource = charaInit.CandidateSource;
            return character;
        }

        internal static CharacterInfo ReadCharacterVitals(
            MemoryReader memory,
            ulong characterAddress)
        {
            CharacterInfo character = new CharacterInfo
            {
                Address = characterAddress,
                LocalId = memory.ReadUInt32(characterAddress + 0x08),
                ParamId = memory.ReadInt32(characterAddress + 0x60),
                GlobalId = memory.ReadUInt16(characterAddress + 0x74)
            };

            ulong characterData = memory.ReadUInt64(characterAddress + 0x190);
            if (!memory.IsLikelyPointer(characterData))
            {
                return character;
            }

            ulong hpObject = memory.ReadUInt64(characterData + 0x00);
            if (memory.IsLikelyPointer(hpObject))
            {
                ulong packedHealth = memory.ReadUInt64(hpObject + 0x138);
                character.CurrentHp = unchecked((int)(packedHealth & 0xFFFFFFFF));
                character.MaxHp = unchecked((int)(packedHealth >> 32));
            }

            character.Stagger = ReadStaggerProbe(memory, characterData);
            return character;
        }

        private static CharaInitProbeResult ReadCharaInitParamProbe(
            MemoryReader memory,
            ulong characterAddress,
            ulong characterData,
            int npcParamId,
            IReadOnlyList<EquipmentItemProbe> targetEquipment,
            IReadOnlyList<EquipmentItemProbe> expectedEquipment,
            CharaInitLoadoutCatalog charaInitLoadouts)
        {
            if (!CouldUseCharaInitParam(npcParamId))
            {
                return CharaInitProbeResult.None;
            }

            CharaInitProbeResult bestCandidate = CharaInitProbeResult.None;
            List<(string Source, ulong Base)> bases = BuildCharaInitProbeBases(
                memory,
                characterAddress,
                characterData);

            if (expectedEquipment.Count < 2)
            {
                LogCharaInitProbeDiagnostics(
                    memory,
                    characterAddress,
                    characterData,
                    npcParamId,
                    targetEquipment,
                    expectedEquipment,
                    charaInitLoadouts,
                    bases,
                    CharaInitProbeResult.None);
                return CharaInitProbeResult.None;
            }

            foreach ((string source, ulong baseAddress) in bases)
            {
                CharaInitProbeResult msbDirect = ScanCharaInitMsbPlacementRange(
                    memory,
                    baseAddress,
                    source,
                    0x00,
                    0x1000,
                    npcParamId,
                    targetEquipment,
                    expectedEquipment,
                    charaInitLoadouts);
                bestCandidate = PickCharaInitCandidate(bestCandidate, msbDirect);
            }

            if (bestCandidate.ParamId > 0)
            {
                return bestCandidate;
            }

            foreach ((string source, ulong baseAddress) in bases)
            {
                CharaInitProbeResult direct = ScanCharaInitCandidateRange(
                    memory,
                    baseAddress,
                    source,
                    0x00,
                    0x800,
                    targetEquipment,
                    expectedEquipment,
                    charaInitLoadouts);
                bestCandidate = PickCharaInitCandidate(bestCandidate, direct);
            }

            LogCharaInitProbeDiagnostics(
                memory,
                characterAddress,
                characterData,
                npcParamId,
                targetEquipment,
                expectedEquipment,
                charaInitLoadouts,
                bases,
                bestCandidate);

            if (bestCandidate.ParamId > 0)
            {
                return bestCandidate;
            }

            CharaInitProbeResult expectedMatch = InferCharaInitFromEquipment(
                "catalog match expected",
                expectedEquipment,
                charaInitLoadouts);
            if (expectedMatch.ParamId > 0)
            {
                return expectedMatch;
            }

            return CharaInitProbeResult.None;
        }

        private static void LogCharaInitProbeDiagnostics(
            MemoryReader memory,
            ulong characterAddress,
            ulong characterData,
            int npcParamId,
            IReadOnlyList<EquipmentItemProbe> targetEquipment,
            IReadOnlyList<EquipmentItemProbe> expectedEquipment,
            CharaInitLoadoutCatalog charaInitLoadouts,
            IReadOnlyList<(string Source, ulong Base)> bases,
            CharaInitProbeResult result)
        {
            string key = $"{npcParamId}:0x{characterAddress:X}";
            lock (CharaInitDiagnosticsLock)
            {
                if (!CharaInitDiagnosticsKeys.Add(key))
                {
                    return;
                }
            }

            try
            {
                List<string> lines = new()
                {
                    $"Param ID: {npcParamId}    Seen: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"Target address: 0x{characterAddress:X}",
                    $"CharacterData: 0x{characterData:X}",
                    $"Resolved: {(result.ParamId > 0 ? result.ParamId.ToString(CultureInfo.InvariantCulture) : "-")}    Source: {result.Source}",
                    "Expected equipment:",
                };

                lines.AddRange(expectedEquipment.Count == 0
                    ? new[] { "  -" }
                    : expectedEquipment.Select(item => $"  {item.Slot}: {item.ItemId}"));

                lines.Add("Live equipment:");
                lines.AddRange(targetEquipment.Count == 0
                    ? new[] { "  -" }
                    : targetEquipment.Select(item => $"  {item.Slot}: {item.ItemId}"));

                lines.Add("Probe bases:");
                foreach ((string source, ulong baseAddress) in bases)
                {
                    lines.Add($"  {source}: 0x{baseAddress:X}");
                }

                lines.Add("MSB-shaped anchors:");
                int anchorCount = 0;
                foreach ((string source, ulong baseAddress) in bases)
                {
                    if (!memory.IsLikelyPointer(baseAddress))
                    {
                        continue;
                    }

                    for (ulong offset = 4; offset <= 0x1000 && anchorCount < 80; offset += 4)
                    {
                        int value = ReadInt32OrZero(memory, baseAddress + offset);
                        if (value != npcParamId)
                        {
                            continue;
                        }

                        int previous = ReadInt32OrZero(memory, baseAddress + offset - 4);
                        int next = ReadInt32OrZero(memory, baseAddress + offset + 4);
                        lines.Add($"  {source}+0x{offset:X}: prev {previous}, npc {value}, next {next}");
                        for (long delta = -0x30; delta <= 0xA0; delta += 4)
                        {
                            ulong address = delta < 0
                                ? baseAddress + offset - (ulong)-delta
                                : baseAddress + offset + (ulong)delta;
                            int nearby = ReadInt32OrZero(memory, address);
                            if (nearby != 0)
                            {
                                string deltaText = delta < 0
                                    ? $"-0x{(-delta):X}"
                                    : $"+0x{delta:X}";
                                lines.Add($"    raw {deltaText}: {nearby}");
                            }
                        }
                        anchorCount++;

                        for (ulong delta = 4; delta <= 0x40; delta += 4)
                        {
                            int candidate = ReadInt32OrZero(memory, baseAddress + offset + delta);
                            if (!charaInitLoadouts.Contains(candidate))
                            {
                                continue;
                            }

                            int expectedScore = ScoreCharaInitCandidate(candidate, expectedEquipment, charaInitLoadouts);
                            int liveScore = ScoreCharaInitCandidate(candidate, targetEquipment, charaInitLoadouts);
                            lines.Add($"    +0x{delta:X}: {candidate} expected {expectedScore} live {liveScore}");
                        }
                    }
                }

                lines.Add("Loose CharaInit-looking ints:");
                int looseCount = 0;
                foreach ((string source, ulong baseAddress) in bases)
                {
                    if (!memory.IsLikelyPointer(baseAddress))
                    {
                        continue;
                    }

                    for (ulong offset = 0; offset <= 0x800 && looseCount < 120; offset += 4)
                    {
                        int candidate = ReadInt32OrZero(memory, baseAddress + offset);
                        if (!charaInitLoadouts.Contains(candidate))
                        {
                            continue;
                        }

                        int expectedScore = ScoreCharaInitCandidate(candidate, expectedEquipment, charaInitLoadouts);
                        int liveScore = ScoreCharaInitCandidate(candidate, targetEquipment, charaInitLoadouts);
                        lines.Add($"  {source}+0x{offset:X}: {candidate} expected {expectedScore} live {liveScore}");
                        looseCount++;
                    }
                }

                lines.Add(string.Empty);
                File.AppendAllLines(
                    Path.Combine(AppContext.BaseDirectory, "CharaInitProbeDiagnostics.txt"),
                    lines);
            }
            catch
            {
                // CharaInit diagnostics are optional and must never affect the live reader.
            }
        }

        private static CharaInitProbeResult ScanCharaInitMsbPlacementRange(
            MemoryReader memory,
            ulong baseAddress,
            string source,
            ulong startOffset,
            ulong length,
            int npcParamId,
            IReadOnlyList<EquipmentItemProbe> targetEquipment,
            IReadOnlyList<EquipmentItemProbe> expectedEquipment,
            CharaInitLoadoutCatalog charaInitLoadouts)
        {
            if (!memory.IsLikelyPointer(baseAddress))
            {
                return CharaInitProbeResult.None;
            }

            ulong endOffset = startOffset + length;
            CharaInitProbeResult bestResult = CharaInitProbeResult.None;
            int bestScore = -1;
            ulong[] charaInitDeltas =
            {
                0x10,
                0x14,
                0x18,
                0x1C,
                0x20,
                0x24,
                0x28
            };

            for (ulong offset = startOffset + 4; offset + 0x30 <= endOffset; offset += 4)
            {
                int value;
                try
                {
                    value = memory.ReadInt32(baseAddress + offset);
                }
                catch
                {
                    continue;
                }

                if (value != npcParamId)
                {
                    continue;
                }

                int previous = ReadInt32OrZero(memory, baseAddress + offset - 4);
                int next = ReadInt32OrZero(memory, baseAddress + offset + 4);
                if (previous != npcParamId && next != npcParamId)
                {
                    continue;
                }

                foreach (ulong delta in charaInitDeltas)
                {
                    int candidate = ReadInt32OrZero(memory, baseAddress + offset + delta);
                    if (!charaInitLoadouts.IsLikelyLiveCandidate(candidate))
                    {
                        continue;
                    }

                    int expectedScore = ScoreCharaInitCandidate(candidate, expectedEquipment, charaInitLoadouts);
                    int liveScore = ScoreCharaInitCandidate(candidate, targetEquipment, charaInitLoadouts);
                    if (expectedEquipment.Count >= 2 && expectedScore < 4)
                    {
                        continue;
                    }

                    int score = 100 + expectedScore + liveScore;
                    if (score <= bestScore)
                    {
                        continue;
                    }

                    string resolvedSource = string.Create(
                        CultureInfo.InvariantCulture,
                        $"MSB {source}+0x{offset:X}->+0x{delta:X} score {score} expected {expectedScore} live {liveScore}"
                    );
                    bestScore = score;
                    bestResult = new CharaInitProbeResult(
                        candidate,
                        resolvedSource,
                        candidate,
                        resolvedSource,
                        score);
                }
            }

            return bestResult;
        }

        private static int ReadInt32OrZero(MemoryReader memory, ulong address)
        {
            try
            {
                return memory.ReadInt32(address);
            }
            catch
            {
                return 0;
            }
        }

        private static List<(string Source, ulong Base)> BuildCharaInitProbeBases(
            MemoryReader memory,
            ulong characterAddress,
            ulong characterData)
        {
            List<(string Source, ulong Base)> bases = new()
            {
                ("character", characterAddress),
                ("characterData", characterData)
            };

            AddCharaInitProbeBase(bases, "character+0x580", ReadPointerOrZero(memory, characterAddress + 0x580));
            AddCharaInitProbePointers(memory, bases, "character ptr", characterAddress, 0x700);
            AddCharaInitProbePointers(memory, bases, "characterData ptr", characterData, 0x700);

            return bases;
        }

        private static void AddCharaInitProbePointers(
            MemoryReader memory,
            List<(string Source, ulong Base)> bases,
            string source,
            ulong ownerBase,
            ulong length)
        {
            if (!memory.IsLikelyPointer(ownerBase))
            {
                return;
            }

            for (ulong offset = 0; offset <= length; offset += 8)
            {
                ulong pointer = ReadPointerOrZero(memory, ownerBase + offset);
                if (memory.IsLikelyPointer(pointer))
                {
                    AddCharaInitProbeBase(bases, $"{source}+0x{offset:X}", pointer);
                }
            }
        }

        private static void AddCharaInitProbeBase(
            List<(string Source, ulong Base)> bases,
            string source,
            ulong baseAddress)
        {
            if (baseAddress == 0 ||
                bases.Count >= 64 ||
                bases.Any(candidate => candidate.Base == baseAddress))
            {
                return;
            }

            bases.Add((source, baseAddress));
        }

        private static CharaInitProbeResult PickCharaInitCandidate(
            CharaInitProbeResult current,
            CharaInitProbeResult candidate)
        {
            return current.ParamId > 0 && current.Score >= candidate.Score
                ? current
                : candidate;
        }

        private static CharaInitProbeResult InferCharaInitFromEquipment(
            string source,
            IReadOnlyList<EquipmentItemProbe> equipment,
            CharaInitLoadoutCatalog charaInitLoadouts)
        {
            if (equipment.Count < 2)
            {
                return CharaInitProbeResult.None;
            }

            int bestParamId = 0;
            int bestScore = 0;
            int secondScore = 0;

            foreach (int paramId in charaInitLoadouts.ParamIds)
            {
                int score = ScoreCharaInitCandidate(paramId, equipment, charaInitLoadouts);
                if (score > bestScore)
                {
                    secondScore = bestScore;
                    bestScore = score;
                    bestParamId = paramId;
                    continue;
                }

                if (score > secondScore)
                {
                    secondScore = score;
                }
            }

            int minimumScore = equipment.Count >= 4 ? 12 : 8;
            if (bestParamId <= 0 || bestScore < minimumScore)
            {
                return CharaInitProbeResult.None;
            }

            string resolvedSource = $"{source} score {bestScore}";
            if (secondScore > 0 && bestScore == secondScore)
            {
                resolvedSource += " tied";
            }

            return new CharaInitProbeResult(
                bestParamId,
                resolvedSource,
                bestParamId,
                resolvedSource,
                bestScore);
        }

        private static CharaInitProbeResult ScanCharaInitCandidateRange(
            MemoryReader memory,
            ulong baseAddress,
            string source,
            ulong startOffset,
            ulong length,
            IReadOnlyList<EquipmentItemProbe> targetEquipment,
            IReadOnlyList<EquipmentItemProbe> expectedEquipment,
            CharaInitLoadoutCatalog charaInitLoadouts)
        {
            if (!memory.IsLikelyPointer(baseAddress))
            {
                return CharaInitProbeResult.None;
            }

            ulong endOffset = startOffset + length;
            CharaInitProbeResult bestResult = CharaInitProbeResult.None;
            int bestScore = -1;
            for (ulong offset = startOffset; offset + 4 <= endOffset; offset += 4)
            {
                int candidate;
                try
                {
                    candidate = memory.ReadInt32(baseAddress + offset);
                }
                catch
                {
                    continue;
                }

                if (charaInitLoadouts.IsLikelyLiveCandidate(candidate))
                {
                    int expectedScore = ScoreCharaInitCandidate(candidate, expectedEquipment, charaInitLoadouts);
                    int liveScore = ScoreCharaInitCandidate(candidate, targetEquipment, charaInitLoadouts);
                    int score = expectedScore > 0 ? expectedScore + liveScore : liveScore;
                    if (expectedEquipment.Count >= 2 && expectedScore < 8)
                    {
                        continue;
                    }

                    if (expectedEquipment.Count < 2 && targetEquipment.Count >= 2 && liveScore < 8)
                    {
                        continue;
                    }

                    string resolvedSource = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{source}+0x{offset:X} score {score} expected {expectedScore} live {liveScore}"
                    );
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestResult = new CharaInitProbeResult(
                        candidate,
                        resolvedSource,
                        candidate,
                        resolvedSource,
                        score
                        );
                    }
                }
            }

            return bestResult;
        }

        private static IReadOnlyList<EquipmentItemProbe> BuildExpectedEquipmentProbes(
            int paramId,
            EnemyLoadoutCatalog? enemyLoadouts,
            EnemyLoadoutCatalog? enemyVisualLoadouts)
        {
            List<EquipmentItemProbe> probes = new();
            HashSet<string> seen = new(StringComparer.Ordinal);

            AddExpectedEquipmentProbes(probes, seen, enemyLoadouts?.Find(paramId));
            AddExpectedEquipmentProbes(probes, seen, enemyVisualLoadouts?.Find(paramId));

            return probes;
        }

        private static void AddExpectedEquipmentProbes(
            List<EquipmentItemProbe> probes,
            HashSet<string> seen,
            IReadOnlyList<EnemyLoadoutSlot>? slots)
        {
            if (slots == null)
            {
                return;
            }

            foreach (EnemyLoadoutSlot slot in slots)
            {
                if (slot.ItemId <= 0 ||
                    slot.ItemId > int.MaxValue ||
                    !seen.Add(BuildSlotItemKey(slot.Slot, slot.ItemId)))
                {
                    continue;
                }

                probes.Add(new EquipmentItemProbe(slot.Slot, (int)slot.ItemId));
            }
        }

        private static int ScoreCharaInitCandidate(
            int paramId,
            IReadOnlyList<EquipmentItemProbe> targetEquipment,
            CharaInitLoadoutCatalog charaInitLoadouts)
        {
            IReadOnlyList<CharaInitLoadoutSlot> slots = charaInitLoadouts.Find(paramId);
            if (slots.Count == 0)
            {
                return 0;
            }

            int score = 0;
            HashSet<string> liveSlotItems = targetEquipment
                .Select(item => BuildSlotItemKey(item.Slot, item.ItemId))
                .ToHashSet(StringComparer.Ordinal);
            HashSet<int> liveItems = targetEquipment
                .Select(item => item.ItemId)
                .ToHashSet();

            foreach (CharaInitLoadoutSlot slot in slots)
            {
                if (liveSlotItems.Contains(BuildSlotItemKey(slot.Slot, slot.ItemId)))
                {
                    score += 4;
                    continue;
                }

                if (slot.ItemId <= int.MaxValue && liveItems.Contains((int)slot.ItemId))
                {
                    score += 1;
                }
            }

            return score;
        }

        internal static bool CouldUseCharaInitParam(int npcParamId)
        {
            return npcParamId > 0;
        }

        internal static bool HasReadableTargetVitals(CharacterInfo target)
        {
            return target.ParamId > 0 &&
                   target.CurrentHp > 0 &&
                   target.MaxHp > 0 &&
                   target.CurrentHp <= target.MaxHp * 4;
        }

        private static StaggerInfo? ReadStaggerProbe(
            MemoryReader memory,
            ulong characterData)
        {
            StaggerInfo? superArmor = ReadStaggerBlock(memory, characterData + 0x40);
            if (superArmor != null)
            {
                return superArmor;
            }

            return ReadStaggerBlock(memory, characterData + 0x48);
        }

        private static StaggerInfo? ReadStaggerBlock(
            MemoryReader memory,
            ulong pointerAddress)
        {
            try
            {
                ulong staggerBase = memory.ReadUInt64(pointerAddress);
                if (!memory.IsLikelyPointer(staggerBase))
                {
                    return null;
                }

                float current = memory.ReadSingle(staggerBase + 0x10);
                float maximum = memory.ReadSingle(staggerBase + 0x14);

                if (!float.IsFinite(current) ||
                    !float.IsFinite(maximum) ||
                    maximum <= 0f ||
                    maximum > 1_000_000f)
                {
                    return null;
                }

                return new StaggerInfo(
                    Math.Clamp(current, 0f, maximum),
                    maximum
                );
            }
            catch
            {
                return null;
            }
        }

        private static IReadOnlyList<EquipmentItemProbe> ReadCharacterEquipmentProbe(
            MemoryReader memory,
            CharacterInfo character,
            ulong worldChrMan,
            ulong characterData,
            GameItemCatalog itemCatalog)
        {
            List<ulong> candidateBases = new();
            AddCandidateBase(candidateBases, ReadPointerOrZero(memory, character.Address + 0x580));
            AddCandidateBase(candidateBases, FindWorldChrManEquipmentBase(memory, worldChrMan, character));

            foreach (ulong candidateBase in candidateBases)
            {
                if (!memory.IsLikelyPointer(candidateBase))
                {
                    continue;
                }

                List<EquipmentItemProbe> items = ReadEquipmentBlock(memory, candidateBase, itemCatalog);
                if (items.Count >= 2)
                {
                    return items;
                }

                items = ScanCatalogEquipment(memory, candidateBase, itemCatalog);
                if (items.Count >= 2)
                {
                    return items;
                }
            }

            return Array.Empty<EquipmentItemProbe>();
        }

        private static void AddCandidateBase(List<ulong> candidates, ulong address)
        {
            if (address != 0 && !candidates.Contains(address))
            {
                candidates.Add(address);
            }
        }

        private static ulong FindWorldChrManEquipmentBase(
            MemoryReader memory,
            ulong worldChrMan,
            CharacterInfo target)
        {
            if (!memory.IsLikelyPointer(worldChrMan))
            {
                return 0;
            }

            ulong characterList = ReadPointerOrZero(memory, worldChrMan + 0x10EF8);
            if (!memory.IsLikelyPointer(characterList))
            {
                return 0;
            }

            for (int index = 0; index < 512; index++)
            {
                ulong characterAddress = ReadPointerOrZero(memory, characterList + (ulong)(index * 0x10));
                if (!memory.IsLikelyPointer(characterAddress))
                {
                    continue;
                }

                try
                {
                    uint localId = memory.ReadUInt32(characterAddress + 0x08);
                    int paramId = memory.ReadInt32(characterAddress + 0x60);
                    if (localId != target.LocalId || paramId != target.ParamId)
                    {
                        continue;
                    }

                    ulong equipmentBase = ReadPointerOrZero(memory, characterAddress + 0x580);
                    if (memory.IsLikelyPointer(equipmentBase))
                    {
                        return equipmentBase;
                    }
                }
                catch
                {
                    // Character list entries can disappear while the game streams actors.
                }
            }

            return 0;
        }

        internal static ulong FindWorldChrManCharacter(
            MemoryReader memory,
            ulong worldChrMan,
            uint localId,
            int targetArea = 0)
        {
            if (localId == 0 || !memory.IsLikelyPointer(worldChrMan))
            {
                return 0;
            }

            ulong chrSetMatch = FindWorldChrManCharacterInChrSet(memory, worldChrMan, localId, targetArea, 0x17420);
            if (memory.IsLikelyPointer(chrSetMatch))
            {
                return chrSetMatch;
            }

            chrSetMatch = FindWorldChrManCharacterInChrSet(memory, worldChrMan, localId, targetArea, 0x17438);
            if (memory.IsLikelyPointer(chrSetMatch))
            {
                return chrSetMatch;
            }

            ulong worldBlockMatch = FindWorldBlockCharacter(memory, worldChrMan, localId, targetArea);
            if (memory.IsLikelyPointer(worldBlockMatch))
            {
                return worldBlockMatch;
            }

            ulong characterList = ReadPointerOrZero(memory, worldChrMan + 0x10EF8);
            if (!memory.IsLikelyPointer(characterList))
            {
                return 0;
            }

            for (int index = 0; index < 512; index++)
            {
                ulong characterAddress = ReadPointerOrZero(memory, characterList + (ulong)(index * 0x10));
                if (!memory.IsLikelyPointer(characterAddress))
                {
                    continue;
                }

                try
                {
                    uint candidateLocalId = memory.ReadUInt32(characterAddress + 0x08);
                    int paramId = memory.ReadInt32(characterAddress + 0x60);
                    if (candidateLocalId == localId && paramId > 0)
                    {
                        return characterAddress;
                    }
                }
                catch
                {
                    // Character list entries can disappear while the game streams actors.
                }
            }

            return 0;
        }

        private static ulong FindWorldChrManCharacterInChrSet(
            MemoryReader memory,
            ulong worldChrMan,
            uint localId,
            int targetArea,
            ulong chrSetOffset)
        {
            ulong chrSet = ReadPointerOrZero(memory, worldChrMan + chrSetOffset);
            if (!memory.IsLikelyPointer(chrSet))
            {
                return 0;
            }

            int count;
            try
            {
                count = memory.ReadInt32(chrSet + 0x20);
            }
            catch
            {
                return 0;
            }

            if (count < 0 || count > 4096)
            {
                return 0;
            }

            for (int index = 0; index <= count; index++)
            {
                ulong entry = chrSet + 0x78 + (ulong)(index * 0x10);
                try
                {
                    ulong handle = memory.ReadUInt64(entry);
                    uint candidateLocalId = unchecked((uint)(handle & 0xFFFFFFFF));
                    int candidateArea = memory.ReadInt32(entry + 0x04);
                    if (candidateLocalId != localId ||
                        (targetArea != 0 && candidateArea != targetArea))
                    {
                        continue;
                    }

                    ulong characterAddress = ReadPointerOrZero(memory, entry + 0x08);
                    if (IsReadableEnemy(memory, characterAddress, localId))
                    {
                        return characterAddress;
                    }
                }
                catch
                {
                    // Streaming can invalidate ChrSet entries mid-read.
                }
            }

            return 0;
        }

        private static ulong FindWorldBlockCharacter(
            MemoryReader memory,
            ulong worldChrMan,
            uint localId,
            int targetArea)
        {
            ulong worldBlock = ReadPointerOrZero(memory, worldChrMan + 0x330);
            int guard = 0;

            while (memory.IsLikelyPointer(worldBlock) && guard++ < 256)
            {
                int count;
                ulong chrSetBase;

                try
                {
                    count = memory.ReadInt32(worldBlock + 0x88);
                    chrSetBase = ReadPointerOrZero(memory, worldBlock + 0x90);
                }
                catch
                {
                    break;
                }

                if (count >= 0 && count <= 4096 && memory.IsLikelyPointer(chrSetBase))
                {
                    for (int index = 0; index <= count; index++)
                    {
                        ulong entry = ReadPointerOrZero(memory, chrSetBase + (ulong)(index * 0x10));
                        if (!memory.IsLikelyPointer(entry))
                        {
                            continue;
                        }

                        try
                        {
                            uint candidateLocalId = memory.ReadUInt32(entry + 0x08);
                            int candidateArea = memory.ReadInt32(entry + 0x0C);
                            if (candidateLocalId == localId &&
                                (targetArea == 0 || candidateArea == targetArea) &&
                                IsReadableEnemy(memory, entry, localId))
                            {
                                return entry;
                            }
                        }
                        catch
                        {
                            // Streaming can invalidate world-block entries mid-read.
                        }
                    }
                }

                ulong next = ReadPointerOrZero(memory, worldBlock + 0x80);
                if (!memory.IsLikelyPointer(next) || next == worldBlock)
                {
                    break;
                }

                worldBlock = next;
            }

            return 0;
        }

        private static bool IsReadableEnemy(MemoryReader memory, ulong characterAddress, uint localId)
        {
            if (!memory.IsLikelyPointer(characterAddress))
            {
                return false;
            }

            try
            {
                return memory.ReadUInt32(characterAddress + 0x08) == localId &&
                       memory.ReadInt32(characterAddress + 0x60) > 0;
            }
            catch
            {
                return false;
            }
        }

        internal static TargetHandleInfo ReadPlayerTargetHandle(MemoryReader memory, ulong worldChrMan)
        {
            if (!memory.IsLikelyPointer(worldChrMan))
            {
                return TargetHandleInfo.None;
            }

            ulong playerIns = ReadPointerOrZero(memory, worldChrMan + 0x1E508);
            if (!memory.IsLikelyPointer(playerIns))
            {
                return TargetHandleInfo.None;
            }

            try
            {
                ulong handle = memory.ReadUInt64(playerIns + 0x6B0);
                if (handle == 0 || handle == ulong.MaxValue)
                {
                    return TargetHandleInfo.None;
                }

                uint localId = unchecked((uint)(handle & 0xFFFFFFFF));
                int area = memory.ReadInt32(playerIns + 0x6B4);
                return localId == 0
                    ? TargetHandleInfo.None
                    : new TargetHandleInfo(handle, localId, area, "PlayerIns target handle");
            }
            catch
            {
                return TargetHandleInfo.None;
            }
        }

        private static ulong ReadPointerOrZero(MemoryReader memory, ulong address)
        {
            try
            {
                return memory.ReadUInt64(address);
            }
            catch
            {
                return 0;
            }
        }

        private static List<EquipmentItemProbe> ReadEquipmentBlock(
            MemoryReader memory,
            ulong baseAddress,
            GameItemCatalog itemCatalog)
        {
            List<EquipmentItemProbe> items = new();
            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach ((string slot, ulong offset) in EquipmentItemProbe.Slots)
            {
                int itemId;

                try
                {
                    itemId = memory.ReadInt32(baseAddress + offset);
                }
                catch
                {
                    continue;
                }

                if (!IsCatalogEquipmentItemId(itemId, itemCatalog) ||
                    !seen.Add(BuildSlotItemKey(slot, itemId)))
                {
                    continue;
                }

                items.Add(new EquipmentItemProbe(slot, itemId));
            }

            return items;
        }

        private static List<EquipmentItemProbe> ScanCatalogEquipment(
            MemoryReader memory,
            ulong baseAddress,
            GameItemCatalog itemCatalog)
        {
            List<EquipmentItemProbe> items = new();
            HashSet<int> seen = new();

            if (!memory.IsLikelyPointer(baseAddress))
            {
                return items;
            }

            for (ulong offset = 0; offset <= 0x1800; offset += 4)
            {
                int itemId;
                try
                {
                    itemId = memory.ReadInt32(baseAddress + offset);
                }
                catch
                {
                    continue;
                }

                if (!IsCatalogEquipmentItemId(itemId, itemCatalog) || !seen.Add(itemId))
                {
                    continue;
                }

                GameItemRecord? record = itemCatalog.Find(itemId);
                string slot = record == null
                    ? $"Scanned +0x{offset:X}"
                    : $"{record.Category} +0x{offset:X}";
                items.Add(new EquipmentItemProbe(slot, itemId));

                if (items.Count >= 12)
                {
                    break;
                }
            }

            return items;
        }

        private static bool IsCatalogEquipmentItemId(int itemId, GameItemCatalog itemCatalog)
        {
            if (!IsLikelyEquipmentItemId(itemId))
            {
                return false;
            }

            GameItemRecord? record = itemCatalog.Find(itemId);
            if (record == null)
            {
                return false;
            }

            return record.Category.Equals("Weapons", StringComparison.OrdinalIgnoreCase) ||
                   record.Category.Equals("Armor", StringComparison.OrdinalIgnoreCase) ||
                   record.Category.Equals("Accessories", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLikelyEquipmentItemId(int itemId)
        {
            return itemId > 0 &&
                   itemId != -1 &&
                   itemId < 100_000_000 &&
                   (itemId < 0x7F00 || itemId > 0x7FFF);
        }

        private void ShowTarget(CharacterInfo target)
        {
            string? imagePath = FindPortrait(target.ParamId);
            string enemyName = _enemyCatalog.FindName(target.ParamId) ??
                (imagePath == null
                    ? "UNKNOWN CHARACTER"
                    : ToDisplayName(Directory.GetParent(imagePath)?.Name ?? "Unknown Character"));

            bool tookDamage = _hasEverShownTarget &&
                              _lastDisplayedHp > 0 &&
                              target.CurrentHp < _lastDisplayedHp;

            _lastDisplayedHp = target.CurrentHp;
            _hasEverShownTarget = true;

            SafeUi(() =>
            {
                _targetIsActive = true;
                (string displayName, string qualifier) = SplitEnemyDisplayName(enemyName);
                _nameLabel.Text = displayName.ToUpperInvariant();
                _nameQualifierLabel.Text = qualifier.ToUpperInvariant();
                _variantLabel.Text = imagePath == null
                    ? "Portrait unavailable"
                    : $"Variant {target.ParamId}";
                _paramLabel.Text = $"PARAM ID  {target.ParamId}";
                _charaInitLabel.Text = target.CharaInitParamId > 0
                    ? $"CHARAINIT  {target.CharaInitParamId}  {target.CharaInitParamSource}"
                    : CouldUseCharaInitParam(target.ParamId)
                        ? "CHARAINIT  —  scanning"
                        : "CHARAINIT  —";
                _rewardsValueLabel.Text = BuildRewardSummary(target.ParamId, target.ClearCount);
                _statusOrb.State = LinkState.Linked;
                _statusLabel.Text = imagePath == null
                    ? "Target found. Portrait asset is missing."
                    : "Live target matched successfully.";
                if (target.CharaInitParamId > 0)
                {
                    _statusLabel.Text = $"Live CharaInitParam {target.CharaInitParamId} resolved.";
                }
                else if (imagePath != null)
                {
                    _statusLabel.Text = "Live target linked. Loadout resolves by Param ID.";
                }

                _hpLabel.Text = $"HEALTH  {target.CurrentHp:N0}  /  {target.MaxHp:N0}";
                _hpBar.Text = $"HP  {target.CurrentHp:N0} / {target.MaxHp:N0}";
                UpdateStaggerUi(target.Stagger);

                int percentage = 0;
                if (target.MaxHp > 0)
                {
                    percentage = (int)Math.Clamp(
                        (long)target.CurrentHp * 100L / target.MaxHp,
                        0,
                        100
                    );
                }

                _hpBar.Value = percentage;
                if (tookDamage)
                {
                    _hpBar.Flash();
                }

                if (imagePath != null && File.Exists(imagePath))
                {
                    Image? oldImage = _portrait.Image;
                    _portrait.Image = LoadImageUnlocked(imagePath);
                    oldImage?.Dispose();
                }

                string loadoutSignature = BuildLoadoutSignature(target);
                if (_displayedLoadoutParamId != target.ParamId ||
                    !string.Equals(_displayedLoadoutSignature, loadoutSignature, StringComparison.Ordinal))
                {
                    _displayedLoadoutParamId = target.ParamId;
                    _displayedLoadoutSignature = loadoutSignature;
                    DisplayLoadout(target, imagePath);
                }
            });
        }

        private string BuildRewardSummary(int paramId, int clearCount)
        {
            EnemyRewardRecord? reward = _enemyRewardCatalog.Find(paramId);
            if (reward == null)
            {
                return "No Param ID reward data";
            }

            string runeText = BuildRuneRewardText(reward.Runes, clearCount, paramId);

            if (reward.Drops.Length == 0)
            {
                return $"{runeText}\r\nNo item drops";
            }

            string dropText = string.Join(
                "\r\n",
                reward.Drops
                    .OrderBy(drop => drop.Slot)
                    .Select(FormatDropLine)
            );

            return $"{runeText}\r\n{dropText}";
        }

        private static string FormatDropLine(EnemyDropRecord drop)
        {
            string quantity = drop.Quantity > 1
                ? $" x{drop.Quantity}"
                : string.Empty;
            return $"{drop.ItemName}{quantity} ({drop.ChancePercent:0.##}%)";
        }

        private string BuildRuneRewardText(int baseRunes, int clearCount, int paramId)
        {
            if (baseRunes <= 0)
            {
                return "No rune reward";
            }

            if (clearCount < 0)
            {
                return $"Runes {baseRunes:N0}  J?";
            }

            RuneReward runeReward = _runeScalingCatalog.Calculate(paramId, baseRunes, clearCount);
            string journey = runeReward.ClearCount == 0
                ? "J1"
                : $"J{runeReward.ClearCount + 1}";

            return runeReward.ClearCount == 0 || Math.Abs(runeReward.Multiplier - 1.0) < 0.0001
                ? $"Runes {baseRunes:N0}  {journey}"
                : $"Runes {runeReward.Runes:N0} ({journey}, base {baseRunes:N0} x{runeReward.Multiplier:0.###})";
        }

        private void SetInactiveState(string message, string state)
        {
            if (!_targetIsActive && string.Equals(_lastStatusMessage, message, StringComparison.Ordinal))
            {
                return;
            }

            _targetIsActive = false;
            _lastStatusMessage = message;

            SafeUi(() =>
            {
                _statusLabel.Text = message;
                _statusOrb.State = state == "OFFLINE" ? LinkState.Offline : LinkState.Linking;

                if (_lastTarget == null)
                {
                    _nameLabel.Text = state == "OFFLINE" ? "WAITING FOR ELDEN RING" : "NO TARGET";
                    _nameQualifierLabel.Text = string.Empty;
                    _variantLabel.Text = "Lock onto an enemy";
                    _paramLabel.Text = "PARAM ID  —";
                    _charaInitLabel.Text = "CHARAINIT  —";
                    _hpLabel.Text = "HEALTH  —";
                    _hpBar.Text = "HP  —";
                    _hpBar.Value = 0;
                    UpdateStaggerUi(null);
                    _loadoutTitleLabel.Text = "REWARDS";
                    _rewardsValueLabel.Text = "Runes resolve by Param ID";
                    RenderLoadoutRail(Array.Empty<ItemInfo>());
                    RenderMagicRail(Array.Empty<ItemInfo>());
                }
            });
        }

        private void UpdateStaggerUi(StaggerInfo? stagger)
        {
            if (stagger == null || stagger.Maximum <= 0f)
            {
                _staggerLabel.Text = "STAGGER  AWAITING MEMORY LINK";
                _staggerLabel.ForeColor = MutedText;
                _staggerBar.Text = "STAGGER  —";
                _staggerBar.Value = 0;
                _hasDisplayedStagger = false;
                _lastDisplayedStagger = 0f;
                return;
            }

            bool staggerBroke = _hasDisplayedStagger &&
                                _lastDisplayedStagger > 0f &&
                                stagger.Current <= 0f;

            int percentage = (int)Math.Clamp(
                Math.Round(stagger.Current * 100f / stagger.Maximum),
                0,
                100
            );

            _staggerLabel.Text = $"STAGGER  {stagger.Current:0.#}  /  {stagger.Maximum:0.#}";
            _staggerLabel.ForeColor = SecondaryText;
            _staggerBar.Text = $"STAGGER  {stagger.Current:0.#} / {stagger.Maximum:0.#}";
            _staggerBar.Value = percentage;

            if (staggerBroke)
            {
                _staggerBar.Flash(Color.FromArgb(222, 176, 64), 5000);
            }

            _lastDisplayedStagger = stagger.Current;
            _hasDisplayedStagger = true;
        }

        private void DisplayLoadout(CharacterInfo target, string? portraitPath)
        {
            IReadOnlyList<ItemInfo> items = LoadLoadoutForEnemy(target, portraitPath);
            IReadOnlyList<ItemInfo> spells = LoadMagicForEnemy(target);
            _loadoutTitleLabel.Text = "REWARDS";
            RenderLoadoutRail(items);
            RenderMagicRail(spells);
        }

        private void RenderLoadoutRail(IReadOnlyList<ItemInfo> items)
        {
            DisposeItemCards();
            _loadoutEmptyLabel.Visible = false;
            _itemFlow.Visible = true;

            HashSet<int> usedIndexes = new();
            List<string> visibleSlots = BuildVisibleLoadoutSlots(items);
            List<Control> cards = new();
            foreach (string slot in visibleSlots)
            {
                int itemIndex = FindItemIndexForSlot(items, slot, usedIndexes);
                Control card = itemIndex >= 0
                    ? BuildItemCard(items[itemIndex])
                    : new EmptySlotControl(slot);

                if (itemIndex >= 0)
                {
                    usedIndexes.Add(itemIndex);
                }

                cards.Add(card);
            }

            for (int index = 0; index < items.Count; index++)
            {
                if (usedIndexes.Contains(index))
                {
                    continue;
                }

                if (!CanAppendExtraRailSlot(cards.Count + 1))
                {
                    continue;
                }

                cards.Add(BuildItemCard(items[index]));
            }

            ApplyRailLayout(_itemFlow, cards);
        }

        private void RenderMagicRail(IReadOnlyList<ItemInfo> spells)
        {
            DisposeSpellCards();
            _magicEmptyLabel.Visible = false;
            _spellFlow.Visible = true;

            HashSet<int> usedIndexes = new();
            List<Control> cards = new();
            foreach (string slot in MagicRailSlots)
            {
                int spellIndex = FindItemIndexForSlot(spells, slot, usedIndexes);
                Control card = spellIndex >= 0
                    ? BuildItemCard(spells[spellIndex])
                    : new EmptySlotControl(slot);

                if (spellIndex >= 0)
                {
                    usedIndexes.Add(spellIndex);
                }

                cards.Add(card);
            }

            ApplyRailLayout(_spellFlow, cards);
        }

        private void RenderItemHistory()
        {
            _itemHistoryEmptyLabel.Visible = _itemHistory.Count == 0;
            _itemHistoryFlow.Visible = _itemHistory.Count > 0;

            List<ItemHistoryEntry> entries = _itemHistory.Take(20).ToList();
            int width = Math.Max(1, _itemHistoryFlow.ClientSize.Width - 2);
            _itemHistoryFlow.SuspendLayout();
            try
            {
                for (int index = 0; index < entries.Count; index++)
                {
                    ItemHistoryEntry entry = entries[index];
                    string key = BuildItemHistoryEntryKey(entry);
                    if (index < _itemHistoryFlow.Controls.Count &&
                        _itemHistoryFlow.Controls[index] is ItemHistoryRowControl existingRow &&
                        string.Equals(existingRow.EntryKey, key, StringComparison.Ordinal))
                    {
                        existingRow.Width = width;
                        continue;
                    }

                    ItemInfo item = LoadItem(entry.ItemId, "Picked Up");
                    var row = new ItemHistoryRowControl(entry, item, key)
                    {
                        Width = width,
                        Height = 58,
                        Margin = new Padding(0, 0, 0, 6)
                    };
                    row.ItemHoverStarted += ShowItemHoverOverlay;
                    row.ItemHoverEnded += HideItemHoverOverlay;

                    if (index < _itemHistoryFlow.Controls.Count)
                    {
                        Control old = _itemHistoryFlow.Controls[index];
                        _itemHistoryFlow.Controls.RemoveAt(index);
                        old.Dispose();
                    }

                    _itemHistoryFlow.Controls.Add(row);
                    _itemHistoryFlow.Controls.SetChildIndex(row, index);
                }

                while (_itemHistoryFlow.Controls.Count > entries.Count)
                {
                    int lastIndex = _itemHistoryFlow.Controls.Count - 1;
                    Control old = _itemHistoryFlow.Controls[lastIndex];
                    _itemHistoryFlow.Controls.RemoveAt(lastIndex);
                    old.Dispose();
                }
            }
            finally
            {
                _itemHistoryFlow.ResumeLayout();
            }
        }

        private static string BuildItemHistoryEntryKey(ItemHistoryEntry entry)
        {
            return string.Join(
                "|",
                entry.ItemId.ToString(CultureInfo.InvariantCulture),
                Math.Max(1, entry.Quantity).ToString(CultureInfo.InvariantCulture),
                entry.Name,
                entry.Category,
                entry.Source
            );
        }

        private void RecordPickedUpItem(long itemId, int quantity = 1)
        {
            if (itemId <= 0)
            {
                return;
            }

            GameItemRecord? record = _itemCatalog.Find(itemId);
            _itemHistory.Insert(0, new ItemHistoryEntry
            {
                ItemId = itemId,
                Name = record?.Name ?? $"ITEM ID {itemId}",
                Category = record?.Category ?? "Unknown",
                Quantity = Math.Max(1, quantity),
                Source = "Feed",
                PickedUpAt = DateTimeOffset.Now
            });

            if (_itemHistory.Count > 20)
            {
                _itemHistory.RemoveRange(20, _itemHistory.Count - 20);
            }

            SaveItemHistory();
            RenderItemHistory();
        }

        private void LoadItemHistory()
        {
            try
            {
                if (!File.Exists(_itemHistoryPath))
                {
                    return;
                }

                ItemHistoryEntry[]? entries = JsonSerializer.Deserialize<ItemHistoryEntry[]>(
                    File.ReadAllText(_itemHistoryPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );

                if (entries == null)
                {
                    return;
                }

                _itemHistory.Clear();
                _itemHistory.AddRange(entries
                    .Where(entry => entry.ItemId > 0)
                    .OrderByDescending(entry => entry.PickedUpAt)
                    .Take(20));
            }
            catch
            {
                // History is helpful but should never interrupt the overlay.
            }
        }

        private void SaveItemHistory()
        {
            try
            {
                File.WriteAllText(
                    _itemHistoryPath,
                    JsonSerializer.Serialize(
                        _itemHistory.Take(20).ToArray(),
                        new JsonSerializerOptions { WriteIndented = true }
                    )
                );
            }
            catch
            {
                // History persistence is optional.
            }
        }

        private void IngestPickedItemFeed()
        {
            try
            {
                if (!File.Exists(_itemPickupFeedPath))
                {
                    return;
                }

                using var stream = new FileStream(
                    _itemPickupFeedPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                );

                if (_itemPickupFeedPosition > stream.Length)
                {
                    _itemPickupFeedPosition = 0;
                }

                stream.Position = _itemPickupFeedPosition;
                using var reader = new StreamReader(stream, Encoding.UTF8, true, leaveOpen: true);
                while (!reader.EndOfStream)
                {
                    string? line = reader.ReadLine();
                    if (TryParsePickedItemLine(line, out long itemId, out int quantity))
                    {
                        RecordPickedUpItem(itemId, quantity);
                    }
                }

                _itemPickupFeedPosition = stream.Position;
            }
            catch
            {
                // Pickup feed is optional and may be written while we read it.
            }
        }

        private void UpdateItemHistoryFromInventory(MemoryReader memory, ulong gameDataMan)
        {
            DateTime now = DateTime.UtcNow;
            if (now < _nextInventoryHistoryScanUtc)
            {
                return;
            }

            _nextInventoryHistoryScanUtc = now.AddSeconds(1);

            try
            {
                IReadOnlyList<InventoryItemSnapshot> snapshots = ReadRecentInventoryItems(
                    memory,
                    gameDataMan,
                    _itemCatalog);

                if (snapshots.Count == 0)
                {
                    WriteInventoryDiagnostics("No inventory items resolved from PlayerGameData.");
                    return;
                }

                string signature = string.Join(
                    "|",
                    snapshots.Select(item => $"{item.ItemId}:{item.Quantity}")
                );
                if (string.Equals(signature, _lastInventoryHistorySignature, StringComparison.Ordinal))
                {
                    return;
                }

                _lastInventoryHistorySignature = signature;
                DateTimeOffset timestamp = DateTimeOffset.Now;
                List<ItemHistoryEntry> entries = snapshots
                    .Select((item, index) => new ItemHistoryEntry
                    {
                        ItemId = item.ItemId,
                        Name = item.Name,
                        Category = item.Category,
                        Quantity = item.Quantity,
                        Source = "Inventory",
                        InventoryIndex = item.InventoryIndex,
                        PickedUpAt = timestamp.AddSeconds(-index)
                    })
                    .ToList();

                SafeUi(() =>
                {
                    _itemHistory.Clear();
                    _itemHistory.AddRange(entries);
                    SaveItemHistory();
                    RenderItemHistory();
                });
            }
            catch (Exception exception) when (exception is MemoryReadException or InvalidOperationException)
            {
                WriteInventoryDiagnostics(exception.Message);
            }
        }

        private static IReadOnlyList<InventoryItemSnapshot> ReadRecentInventoryItems(
            MemoryReader memory,
            ulong gameDataMan,
            GameItemCatalog itemCatalog)
        {
            if (!memory.IsLikelyPointer(gameDataMan))
            {
                return Array.Empty<InventoryItemSnapshot>();
            }

            ulong playerGameData = memory.ReadUInt64(gameDataMan + 0x8);
            if (!memory.IsLikelyPointer(playerGameData))
            {
                return Array.Empty<InventoryItemSnapshot>();
            }

            ulong equipInventoryData = memory.ReadUInt64(playerGameData + PlayerInventoryOffset);
            if (!memory.IsLikelyPointer(equipInventoryData))
            {
                return Array.Empty<InventoryItemSnapshot>();
            }

            ulong inventoryList = memory.ReadUInt64(equipInventoryData + 0x10);
            int inventoryNum = memory.ReadInt32(equipInventoryData + 0x18);
            if (!memory.IsLikelyPointer(inventoryList) || inventoryNum <= 0)
            {
                return Array.Empty<InventoryItemSnapshot>();
            }

            byte[] inventoryBytes;
            try
            {
                inventoryBytes = memory.ReadBytes(
                    inventoryList,
                    checked(PlayerInventoryCapacity * InventoryEntrySize));
            }
            catch (MemoryReadException)
            {
                return Array.Empty<InventoryItemSnapshot>();
            }

            List<InventoryItemSnapshot> items = new();
            int occupiedCount = 0;

            for (int index = 0; index < PlayerInventoryCapacity; index++)
            {
                int offset = index * InventoryEntrySize;
                uint handle = BitConverter.ToUInt32(inventoryBytes, offset);
                uint encodedId = BitConverter.ToUInt32(inventoryBytes, offset + 0x4);
                int quantity = BitConverter.ToInt32(inventoryBytes, offset + 0x8);

                if (handle == 0 ||
                    encodedId == 0 ||
                    encodedId == uint.MaxValue ||
                    quantity <= 0)
                {
                    continue;
                }

                if (!TryDecodeInventoryItem(encodedId, itemCatalog, out long itemId, out GameItemRecord? record))
                {
                    continue;
                }

                occupiedCount++;
                GameItemRecord resolvedRecord = record!;
                items.Add(new InventoryItemSnapshot(
                    index,
                    itemId,
                    Math.Clamp(quantity, 1, 9999),
                    string.IsNullOrWhiteSpace(resolvedRecord.Name) ? $"ITEM ID {itemId}" : resolvedRecord.Name,
                    string.IsNullOrWhiteSpace(resolvedRecord.Category) ? "Unknown" : resolvedRecord.Category
                ));

                if (occupiedCount >= inventoryNum)
                {
                    break;
                }
            }

            return items
                .OrderByDescending(item => item.InventoryIndex)
                .Take(20)
                .ToArray();
        }

        private static bool TryDecodeInventoryItem(
            uint encodedId,
            GameItemCatalog itemCatalog,
            out long itemId,
            out GameItemRecord? record)
        {
            itemId = 0;
            record = null;

            uint typePrefix = encodedId & 0xF0000000;
            long rawItemId = encodedId & 0x0FFFFFFF;
            if (rawItemId <= 0)
            {
                return false;
            }

            long candidateId = rawItemId;
            if (typePrefix == 0x00000000)
            {
                GameItemRecord? exactWeapon = itemCatalog.Find(rawItemId);
                if (exactWeapon != null && IsInventoryCatalogCategory(exactWeapon.Category))
                {
                    itemId = rawItemId;
                    record = exactWeapon;
                    return true;
                }

                candidateId = (rawItemId / 100) * 100;
            }
            else if (typePrefix is 0x10000000 or 0x20000000 or 0x40000000)
            {
                candidateId = rawItemId;
            }
            else
            {
                return false;
            }

            record = itemCatalog.Find(candidateId);
            if (record == null || !IsInventoryCatalogCategory(record.Category))
            {
                return false;
            }

            itemId = candidateId;
            return true;
        }

        private static bool IsInventoryCatalogCategory(string category)
        {
            return category.Equals("Goods", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("Weapons", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("Armor", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("Accessories", StringComparison.OrdinalIgnoreCase);
        }

        private void WriteInventoryDiagnostics(string message)
        {
            if (string.Equals(message, _lastInventoryDiagnostics, StringComparison.Ordinal))
            {
                return;
            }

            _lastInventoryDiagnostics = message;

            try
            {
                File.WriteAllText(
                    _inventoryDiagnosticsPath,
                    $"{DateTimeOffset.Now:O}\r\n{message}\r\n"
                );
            }
            catch
            {
                // Diagnostics should never interrupt the overlay.
            }
        }

        private static bool TryParsePickedItemLine(string? line, out long itemId, out int quantity)
        {
            itemId = 0;
            quantity = 1;
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            string value = line.Split('#', 2)[0].Trim();
            if (value.Length == 0)
            {
                return false;
            }

            string[] parts = value
                .Replace("x", " ", StringComparison.OrdinalIgnoreCase)
                .Replace(",", " ")
                .Replace(";", " ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length == 0 ||
                !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out itemId) ||
                itemId <= 0)
            {
                return false;
            }

            if (parts.Length > 1 &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedQuantity))
            {
                quantity = Math.Max(1, parsedQuantity);
            }

            return true;
        }

        private void DisposeItemHistoryCards()
        {
            foreach (Control control in _itemHistoryFlow.Controls.Cast<Control>().ToArray())
            {
                control.Dispose();
            }

            _itemHistoryFlow.Controls.Clear();
        }

        private static List<string> BuildVisibleLoadoutSlots(IReadOnlyList<ItemInfo> items)
        {
            List<string> slots = LoadoutRailSlots.ToList();
            HashSet<string> normalizedSlots = new(
                slots.Select(NormalizeSlotName),
                StringComparer.Ordinal
            );

            foreach (string slot in ConditionalLoadoutRailSlots)
            {
                string normalized = NormalizeSlotName(slot);
                if (normalizedSlots.Contains(normalized))
                {
                    continue;
                }

                if (items.Any(item => NormalizeSlotName(item.Slot) == normalized))
                {
                    slots.Add(slot);
                    normalizedSlots.Add(normalized);
                }
            }

            return slots;
        }

        private ItemCardControl BuildItemCard(ItemInfo item)
        {
            var card = new ItemCardControl(item);

            card.ItemHoverStarted += ShowItemHoverOverlay;
            card.ItemHoverEnded += HideItemHoverOverlay;
            return card;
        }

        private static int FindItemIndexForSlot(
            IReadOnlyList<ItemInfo> items,
            string slot,
            HashSet<int> usedIndexes)
        {
            string normalizedSlot = NormalizeSlotName(slot);

            for (int index = 0; index < items.Count; index++)
            {
                if (usedIndexes.Contains(index))
                {
                    continue;
                }

                if (NormalizeSlotName(items[index].Slot) == normalizedSlot)
                {
                    return index;
                }
            }

            return -1;
        }

        private static string BuildSlotItemKey(string slot, long itemId)
        {
            return $"{NormalizeSlotName(slot)}:{itemId}";
        }

        private static string NormalizeSlotName(string value)
        {
            string normalized = value
                .Trim()
                .ToLowerInvariant()
                .Replace("gauntlets", "gauntlet")
                .Replace("gloves", "gauntlet")
                .Replace("greaves", "leggings")
                .Replace("legs", "leggings")
                .Replace("-", " ");

            return string.Join(
                " ",
                normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            );
        }

        private IReadOnlyList<ItemInfo> LoadLoadoutForEnemy(CharacterInfo target, string? portraitPath)
        {
            int paramId = target.ParamId;
            string cacheKey = BuildLoadoutCacheKey(target);

            if (_loadoutCache.TryGetValue(cacheKey, out IReadOnlyList<ItemInfo>? cached))
            {
                return cached;
            }

            IReadOnlyList<ItemInfo> databaseItems = LoadVerifiedEnemyLoadout(paramId);
            if (databaseItems.Count > 0)
            {
                databaseItems = MergeMissingLiveEquipmentSlots(databaseItems, target);
                _loadoutCache[cacheKey] = databaseItems;
                return databaseItems;
            }

            IReadOnlyList<ItemInfo> visualItems = LoadVisualEnemyLoadout(paramId);
            if (visualItems.Count > 0)
            {
                visualItems = MergeMissingLiveEquipmentSlots(visualItems, target);
                _loadoutCache[cacheKey] = visualItems;
                return visualItems;
            }

            string? loadoutPath = FindLoadoutFile(paramId, portraitPath);
            if (loadoutPath != null)
            {
                IReadOnlyList<ItemInfo> fileItems = LoadLoadoutFile(loadoutPath);
                if (fileItems.Count > 0)
                {
                    fileItems = MergeMissingLiveEquipmentSlots(fileItems, target);
                    _loadoutCache[cacheKey] = fileItems;
                    return fileItems;
                }
            }

            IReadOnlyList<ItemInfo> charaInitItems = target.CharaInitParamId > 0
                ? LoadGeneratedCharaInitLoadout(target.CharaInitParamId)
                : Array.Empty<ItemInfo>();
            if (charaInitItems.Count > 0)
            {
                charaInitItems = MergeMissingLiveEquipmentSlots(charaInitItems, target);
                _loadoutCache[cacheKey] = charaInitItems;
                return charaInitItems;
            }

            IReadOnlyList<ItemInfo> empty = Array.Empty<ItemInfo>();
            _loadoutCache[cacheKey] = empty;
            return empty;
        }

        private static string BuildLoadoutCacheKey(CharacterInfo target)
        {
            List<EquipmentItemProbe> trustedEquipment = target.Equipment
                .Where(item => IsTrustedLiveEquipmentSlot(item.Slot))
                .ToList();

            if (trustedEquipment.Count == 0)
            {
                return target.ParamId.ToString(CultureInfo.InvariantCulture);
            }

            string equipmentSignature = string.Join(
                "|",
                trustedEquipment.Select(item =>
                    $"{NormalizeSlotName(item.Slot)}={item.ItemId.ToString(CultureInfo.InvariantCulture)}")
            );

            return $"{target.ParamId.ToString(CultureInfo.InvariantCulture)}|{equipmentSignature}";
        }

        private IReadOnlyList<ItemInfo> MergeMissingLiveEquipmentSlots(
            IReadOnlyList<ItemInfo> baseItems,
            CharacterInfo target)
        {
            if (target.Equipment.Count == 0)
            {
                return baseItems;
            }

            List<ItemInfo> merged = baseItems.ToList();
            HashSet<string> occupiedSlots = new(
                merged.Select(item => NormalizeSlotName(item.Slot)),
                StringComparer.Ordinal
            );

            foreach (EquipmentItemProbe liveItem in target.Equipment)
            {
                if (!IsTrustedLiveEquipmentSlot(liveItem.Slot))
                {
                    continue;
                }

                string normalizedSlot = NormalizeSlotName(liveItem.Slot);
                if (occupiedSlots.Contains(normalizedSlot))
                {
                    continue;
                }

                merged.Add(LoadItem(liveItem.ItemId, liveItem.Slot));
                occupiedSlots.Add(normalizedSlot);
            }

            return merged;
        }

        private IReadOnlyList<ItemInfo> LoadVerifiedEnemyLoadout(int paramId)
        {
            IReadOnlyList<EnemyLoadoutSlot> slots = _enemyLoadoutCatalog.Find(paramId);
            if (slots.Count == 0)
            {
                return Array.Empty<ItemInfo>();
            }

            List<ItemInfo> items = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (EnemyLoadoutSlot slot in slots)
            {
                if (slot.ItemId <= 0 || !seen.Add(BuildSlotItemKey(slot.Slot, slot.ItemId)))
                {
                    continue;
                }

                items.Add(LoadItem(slot.ItemId, slot.Slot, slot.Note));
            }

            return items;
        }

        private IReadOnlyList<ItemInfo> LoadVisualEnemyLoadout(int paramId)
        {
            IReadOnlyList<EnemyLoadoutSlot> slots = _enemyVisualLoadoutCatalog.Find(paramId);
            if (slots.Count == 0)
            {
                return Array.Empty<ItemInfo>();
            }

            List<ItemInfo> items = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (EnemyLoadoutSlot slot in slots)
            {
                if (slot.ItemId <= 0 || !seen.Add(BuildSlotItemKey(slot.Slot, slot.ItemId)))
                {
                    continue;
                }

                items.Add(LoadItem(slot.ItemId, slot.Slot, slot.Note));
            }

            return items;
        }

        private IReadOnlyList<ItemInfo> LoadLoadoutFile(string loadoutPath)
        {
            List<ItemInfo> items = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            try
            {
                foreach (string rawLine in File.ReadLines(loadoutPath))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//"))
                    {
                        continue;
                    }

                    string slot = "EQUIPPED";
                    string value = line;
                    int separator = line.IndexOf('=');
                    if (separator >= 0)
                    {
                        slot = line[..separator].Trim();
                        value = line[(separator + 1)..].Trim();
                    }

                    foreach (string token in value.Split(
                                 new[] { ',', ';', '|', ' ', '\t' },
                                 StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long itemId) ||
                            itemId <= 0 ||
                            !seen.Add(BuildSlotItemKey(slot, itemId)))
                        {
                            continue;
                        }

                        items.Add(LoadItem(itemId, slot));
                    }
                }
            }
            catch
            {
                // A malformed optional loadout file should not interrupt the live reader.
            }

            return items;
        }

        private IReadOnlyList<ItemInfo> LoadGeneratedCharaInitLoadout(int paramId)
        {
            IReadOnlyList<CharaInitLoadoutSlot> slots = _charaInitLoadouts.Find(paramId);
            if (slots.Count == 0)
            {
                return Array.Empty<ItemInfo>();
            }

            List<ItemInfo> items = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (CharaInitLoadoutSlot slot in slots)
            {
                if (slot.ItemId <= 0 ||
                    IsMagicSlot(slot.Slot) ||
                    !seen.Add(BuildSlotItemKey(slot.Slot, slot.ItemId)))
                {
                    continue;
                }

                items.Add(LoadItem(
                    slot.ItemId,
                    slot.Slot,
                    quantity: slot.Quantity,
                    categoryOverride: slot.Category));
            }

            return items;
        }

        private IReadOnlyList<ItemInfo> LoadMagicForEnemy(CharacterInfo target)
        {
            IReadOnlyList<EnemyLoadoutSlot> abilitySlots = _enemyMagicCatalog.Find(target.ParamId);
            if (abilitySlots.Count > 0)
            {
                List<ItemInfo> abilities = new();
                HashSet<string> seen = new(StringComparer.Ordinal);
                foreach (EnemyLoadoutSlot slot in abilitySlots)
                {
                    if (slot.ItemId <= 0 || !seen.Add(BuildSlotItemKey(slot.Slot, slot.ItemId)))
                    {
                        continue;
                    }

                    abilities.Add(LoadItem(slot.ItemId, slot.Slot, slot.Note));
                }

                if (abilities.Count > 0)
                {
                    return abilities;
                }
            }

            return target.CharaInitParamId > 0
                ? LoadGeneratedCharaInitSpells(target.CharaInitParamId)
                : Array.Empty<ItemInfo>();
        }

        private IReadOnlyList<ItemInfo> LoadGeneratedCharaInitSpells(int paramId)
        {
            IReadOnlyList<CharaInitLoadoutSlot> slots = _charaInitLoadouts.Find(paramId);
            if (slots.Count == 0)
            {
                return Array.Empty<ItemInfo>();
            }

            List<ItemInfo> items = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (CharaInitLoadoutSlot slot in slots)
            {
                if (slot.ItemId <= 0 ||
                    !IsMagicSlot(slot.Slot) ||
                    !seen.Add(BuildSlotItemKey(slot.Slot, slot.ItemId)))
                {
                    continue;
                }

                items.Add(LoadItem(
                    slot.ItemId,
                    slot.Slot,
                    quantity: slot.Quantity,
                    categoryOverride: slot.Category));
            }

            return items;
        }

        private static bool IsMagicSlot(string? slot)
        {
            return !string.IsNullOrWhiteSpace(slot) &&
                   NormalizeSlotName(slot).StartsWith("spell ", StringComparison.Ordinal);
        }

        private IReadOnlyList<ItemInfo> LoadInferredEquipmentForEnemy(CharacterInfo target, string? portraitPath)
        {
            string enemyName = GetEnemyNameFromPortraitPath(portraitPath);

            List<(string Slot, string Name)> template = EnemyLoadoutTemplates.Find(enemyName, target.ParamId);
            if (template.Count == 0)
            {
                return Array.Empty<ItemInfo>();
            }

            List<ItemInfo> items = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach ((string slot, string name) in template)
            {
                GameItemRecord? record = _itemCatalog.FindByName(name);
                if (record == null || !seen.Add(BuildSlotItemKey(slot, record.ItemId)))
                {
                    continue;
                }

                items.Add(LoadItem(record.ItemId, slot));
            }

            return items;
        }

        private IReadOnlyList<ItemInfo> LoadLiveEquipmentForEnemy(CharacterInfo target)
        {
            if (target.Equipment.Count == 0)
            {
                return Array.Empty<ItemInfo>();
            }

            List<ItemInfo> items = new();
            foreach (EquipmentItemProbe item in target.Equipment)
            {
                if (!IsTrustedLiveEquipmentSlot(item.Slot))
                {
                    continue;
                }

                items.Add(LoadItem(item.ItemId, item.Slot));
            }

            return items;
        }

        private static bool IsTrustedLiveEquipmentSlot(string slot)
        {
            if (string.IsNullOrWhiteSpace(slot) ||
                slot.Contains("+0x", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string normalized = NormalizeSlotName(slot);
            if (normalized.StartsWith("accessory ", StringComparison.Ordinal) ||
                normalized.StartsWith("talisman ", StringComparison.Ordinal))
            {
                return false;
            }

            return normalized is
                "primary right" or
                "primary left" or
                "secondary right" or
                "secondary left" or
                "tertiary right" or
                "tertiary left" or
                "primary arrow" or
                "primary bolt" or
                "secondary arrow" or
                "secondary bolt" or
                "tertiary arrow" or
                "tertiary bolt" or
                "helmet" or
                "armor" or
                "gauntlet" or
                "leggings";
        }

        private static string GetEnemyNameFromPortraitPath(string? portraitPath)
        {
            if (string.IsNullOrWhiteSpace(portraitPath))
            {
                return string.Empty;
            }

            string? folder = Path.GetDirectoryName(portraitPath);
            return folder == null
                ? string.Empty
                : ToDisplayName(Path.GetFileName(folder));
        }

        private static string? FindLoadoutFile(int paramId, string? portraitPath)
        {
            List<string> candidates = new();

            if (!string.IsNullOrWhiteSpace(portraitPath))
            {
                string? folder = Path.GetDirectoryName(portraitPath);
                if (folder != null)
                {
                    candidates.Add(Path.Combine(folder, $"{paramId}.loadout.txt"));
                    candidates.Add(Path.Combine(folder, $"{paramId}.txt"));
                }
            }

            string loadoutRoot = Path.Combine(AppContext.BaseDirectory, "assets", "Loadouts");
            candidates.Add(Path.Combine(loadoutRoot, $"{paramId}.txt"));
            candidates.Add(Path.Combine(loadoutRoot, paramId.ToString(CultureInfo.InvariantCulture), $"{paramId}.txt"));
            candidates.Add(Path.Combine(loadoutRoot, paramId.ToString(CultureInfo.InvariantCulture), "loadout.txt"));

            return candidates.FirstOrDefault(path =>
            {
                try
                {
                    return File.Exists(path) && new FileInfo(path).Length > 0;
                }
                catch
                {
                    return false;
                }
            });
        }

        private ItemInfo LoadItem(
            long itemId,
            string slot,
            string? displayNameOverride = null,
            int quantity = 1,
            string? categoryOverride = null)
        {
            string displaySlot = string.IsNullOrWhiteSpace(slot) ? "EQUIPPED" : ToDisplayName(slot);
            string cacheKey = string.Join(
                "|",
                itemId.ToString(CultureInfo.InvariantCulture),
                displaySlot,
                displayNameOverride?.Trim() ?? string.Empty,
                Math.Max(1, quantity).ToString(CultureInfo.InvariantCulture),
                categoryOverride?.Trim() ?? string.Empty
            );
            if (_itemInfoCache.TryGetValue(cacheKey, out ItemInfo? cachedItem))
            {
                return cachedItem;
            }

            GameItemRecord? gameRecord = _itemCatalog.Find(itemId, categoryOverride);

            string folder = FindItemFolder(itemId) ?? Path.Combine(
                AppContext.BaseDirectory,
                "assets",
                "Items",
                gameRecord?.Category ?? "Unknown",
                itemId.ToString(CultureInfo.InvariantCulture)
            );

            string? imagePath = gameRecord?.IconId is int catalogIconId && catalogIconId >= 0
                ? TryCopyHighQualityItemArt(catalogIconId, folder)
                : null;

            imagePath ??= FindFirstExisting(
                Path.Combine(folder, "image.png"),
                Path.Combine(folder, $"{itemId}.png"),
                Path.Combine(folder, $"{itemId} .png"),
                Path.Combine(folder, "icon.png"),
                File.Exists(gameRecord?.ImagePath) ? gameRecord?.ImagePath : null
            ) ?? FindFirstPng(folder);

            imagePath ??= TryExtractItemIcon(itemId, folder, gameRecord);

            if (imagePath == null)
            {
                imagePath = FindFirstExisting(
                    Path.Combine(AppContext.BaseDirectory, "assets", "placeholders", "missing-item.png"),
                    Path.Combine(AppContext.BaseDirectory, "assets", "placeholders", "item.png")
                );
            }

            ItemInfo item = new ItemInfo
            {
                ItemId = itemId,
                Slot = displaySlot,
                Name = string.IsNullOrWhiteSpace(displayNameOverride)
                    ? ReadTextOrDefault(
                        Path.Combine(folder, "name.txt"),
                        gameRecord?.Name ?? $"ITEM ID {itemId}"
                    )
                    : displayNameOverride.Trim(),
                Description = ResolveItemDescription(
                    Path.Combine(folder, "description.txt"),
                    gameRecord?.Description,
                    gameRecord == null
                        ? $"Live equipment ID {itemId} read from the {ToDisplayName(slot)} slot."
                        : "Description unavailable."
                ),
                Effect = string.IsNullOrWhiteSpace(gameRecord?.Effect) ? null : gameRecord.Effect.Trim(),
                ImagePath = imagePath,
                FolderPath = folder,
                Category = string.IsNullOrWhiteSpace(categoryOverride)
                    ? gameRecord?.Category ?? "Unknown"
                    : categoryOverride.Trim(),
                Quantity = Math.Max(1, quantity)
            };
            _itemInfoCache[cacheKey] = item;
            return item;
        }

        private string? TryExtractItemIcon(long itemId, string folder, GameItemRecord? gameRecord)
        {
            foreach (int iconId in EnumerateCandidateIconIds(itemId, folder, gameRecord))
            {
                string? looseIconPath = TryCopyLooseMenuIcon(iconId, folder);
                if (looseIconPath != null)
                {
                    return looseIconPath;
                }

                if (_iconExtractor == null || !_iconExtractor.ContainsIcon(iconId))
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(folder);
                    return _iconExtractor.ExtractIconToPng(
                        iconId,
                        Path.Combine(folder, "image.png")
                    );
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static string? TryCopyLooseMenuIcon(int iconId, string folder)
        {
            string iconName = $"MENU_ItemIcon_{iconId:D5}.png";
            foreach (string directory in LooseIconDirectories)
            {
                string sourcePath = Path.Combine(directory, iconName);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(folder);
                    string targetPath = Path.Combine(folder, "image.png");
                    CopyFileIfNeeded(sourcePath, targetPath);
                    return targetPath;
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static string? TryCopyHighQualityItemArt(int iconId, string folder)
        {
            string imageName = $"MENU_Knowledge_{iconId:D5}.png";
            foreach (string directory in HighQualityItemArtDirectories)
            {
                string sourcePath = Path.Combine(directory, imageName);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(folder);
                    string targetPath = Path.Combine(folder, "image.png");
                    CopyFileIfNeeded(sourcePath, targetPath);
                    return targetPath;
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static void CopyFileIfNeeded(string sourcePath, string targetPath)
        {
            if (File.Exists(targetPath))
            {
                try
                {
                    FileInfo source = new(sourcePath);
                    FileInfo target = new(targetPath);
                    if (source.Length == target.Length &&
                        source.LastWriteTimeUtc == target.LastWriteTimeUtc)
                    {
                        return;
                    }
                }
                catch
                {
                    // Fall through and let File.Copy report the real problem if there is one.
                }
            }

            File.Copy(sourcePath, targetPath, true);
        }

        private static IEnumerable<int> EnumerateCandidateIconIds(
            long itemId,
            string folder,
            GameItemRecord? gameRecord)
        {
            string iconIdPath = Path.Combine(folder, "iconId.txt");
            int explicitIconId = ReadIntegerOrDefault(iconIdPath, -1);
            if (explicitIconId >= 0)
            {
                yield return explicitIconId;
            }

            if (gameRecord?.IconId is int catalogIconId && catalogIconId >= 0)
            {
                yield return catalogIconId;
            }
        }

        private static string? FindItemFolder(long itemId)
        {
            string root = Path.Combine(AppContext.BaseDirectory, "assets", "Items");
            if (!Directory.Exists(root))
            {
                return null;
            }

            string direct = Path.Combine(root, itemId.ToString(CultureInfo.InvariantCulture));
            if (Directory.Exists(direct))
            {
                return direct;
            }

            try
            {
                return Directory
                    .EnumerateDirectories(root, itemId.ToString(CultureInfo.InvariantCulture), SearchOption.AllDirectories)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static string BuildLoadoutSignature(CharacterInfo target)
        {
            if (target.Equipment.Count == 0)
            {
                return $"fallback:{target.ParamId}:{target.CharaInitParamId}";
            }

            return string.Join(
                "|",
                target.Equipment.Select(item =>
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{item.Slot}:{item.ItemId}"
                    ))
            );
        }

        private static bool EquipmentEquals(
            IReadOnlyList<EquipmentItemProbe> left,
            IReadOnlyList<EquipmentItemProbe> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int index = 0; index < left.Count; index++)
            {
                if (!string.Equals(left[index].Slot, right[index].Slot, StringComparison.Ordinal) ||
                    left[index].ItemId != right[index].ItemId)
                {
                    return false;
                }
            }

            return true;
        }

        private static string? FindFirstPng(string folder)
        {
            if (!Directory.Exists(folder))
            {
                return null;
            }

            try
            {
                return Directory
                    .EnumerateFiles(folder, "*.png", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => Path.GetFileName(path).Length)
                    .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static int ReadIntegerOrDefault(string path, int fallback)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return fallback;
                }

                string value = File.ReadAllText(path).Trim();
                return int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int result)
                    ? result
                    : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static string ReadTextOrDefault(string path, string fallback)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return fallback;
                }

                string value = File.ReadAllText(path).Trim();
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch
            {
                return fallback;
            }
        }

        private static string ResolveItemDescription(string path, string? catalogDescription, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(catalogDescription))
            {
                return catalogDescription.Trim();
            }

            string fileDescription = ReadTextOrDefault(path, string.Empty);
            if (!IsPlaceholderDescription(fileDescription))
            {
                return fileDescription;
            }

            return fallback;
        }

        private static bool IsPlaceholderDescription(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            string normalized = value.Trim();
            return normalized.Equals("Description unavailable.", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Description pending extraction.", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Pending extraction.", StringComparison.OrdinalIgnoreCase);
        }

        private static string? FindFirstExisting(params string?[] paths) =>
            paths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));

        private void ShowItemHoverOverlay(Control source, ItemInfo item)
        {
            _activeHoverSource = source;
            _itemHoverOverlay ??= new ItemHoverOverlayForm();
            if (_itemHoverOverlay.Parent == null)
            {
                Controls.Add(_itemHoverOverlay);
            }

            _itemHoverOverlay.SetItem(item);

            Rectangle bounds = ClientRectangle;
            Rectangle sourceBounds = RectangleToClient(source.RectangleToScreen(source.ClientRectangle));
            Rectangle flowBounds = RectangleToClient(_itemFlow.RectangleToScreen(_itemFlow.ClientRectangle));
            int availableWidth = Math.Max(1, bounds.Width - 16);
            int availableHeight = Math.Max(1, bounds.Height - 16);
            int overlayWidth = availableWidth >= 540
                ? 500
                : Math.Clamp(availableWidth - 16, 300, 420);
            int overlayHeight = availableHeight >= 220
                ? Math.Min(300, availableHeight)
                : availableHeight;

            _itemHoverOverlay.Size = new Size(
                overlayWidth,
                overlayHeight
            );

            int minX = bounds.Left + 8;
            int minY = bounds.Top + 8;
            int maxX = Math.Max(minX, bounds.Right - _itemHoverOverlay.Width - 8);
            int maxY = Math.Max(minY, bounds.Bottom - _itemHoverOverlay.Height - 8);

            int x = maxX;
            int y = flowBounds.Top - _itemHoverOverlay.Height - 10;
            if (y < minY)
            {
                y = sourceBounds.Top - _itemHoverOverlay.Height - 10;
            }

            if (y < minY)
            {
                y = sourceBounds.Bottom + 10;
            }

            if (y + _itemHoverOverlay.Height > bounds.Bottom - 8)
            {
                y = maxY;
            }

            Rectangle overlayBounds = new(x, y, _itemHoverOverlay.Width, _itemHoverOverlay.Height);
            if (overlayBounds.IntersectsWith(sourceBounds))
            {
                int leftX = sourceBounds.Left - _itemHoverOverlay.Width - 10;
                if (leftX >= minX)
                {
                    x = leftX;
                }
            }

            x = Math.Clamp(x, minX, maxX);
            y = Math.Clamp(y, minY, maxY);
            _itemHoverOverlay.Location = new Point(x, y);
            _itemHoverOverlay.BringToFront();

            if (!_itemHoverOverlay.Visible)
            {
                _itemHoverOverlay.Show();
            }

            _hoverOverlayTimer.Start();
        }

        private void HideItemHoverOverlay(Control? source = null)
        {
            if (_itemHoverOverlay == null)
            {
                return;
            }

            if (source != null)
            {
                BeginInvoke(new Action(CheckHoverOverlayDismiss));
                return;
            }

            _hoverOverlayTimer.Stop();
            _activeHoverSource = null;
            _itemHoverOverlay.Hide();
        }

        private void CheckHoverOverlayDismiss()
        {
            if (_itemHoverOverlay == null || !_itemHoverOverlay.Visible)
            {
                _hoverOverlayTimer.Stop();
                _activeHoverSource = null;
                return;
            }

            Point mouse = Control.MousePosition;
            bool overSource = _activeHoverSource != null &&
                !_activeHoverSource.IsDisposed &&
                _activeHoverSource.ClientRectangle.Contains(_activeHoverSource.PointToClient(mouse));
            bool overOverlay =
                _itemHoverOverlay.ClientRectangle.Contains(_itemHoverOverlay.PointToClient(mouse));

            if (overSource || overOverlay)
            {
                return;
            }

            _hoverOverlayTimer.Stop();
            _activeHoverSource = null;
            _itemHoverOverlay.Hide();
        }

        private void ResizeItemCards()
        {
            ApplyLoadoutRailLayout(_itemFlow.Controls.Cast<Control>().ToList());
            ApplyRailLayout(_spellFlow, _spellFlow.Controls.Cast<Control>().ToList());
            ResizeItemHistoryRows();
        }

        private void ResizeItemHistoryRows()
        {
            int width = Math.Max(1, _itemHistoryFlow.ClientSize.Width - 2);
            foreach (Control control in _itemHistoryFlow.Controls)
            {
                control.Width = width;
            }
        }

        private void ApplyLoadoutRailLayout(IReadOnlyList<Control> cards)
        {
            ApplyRailLayout(_itemFlow, cards);
        }

        private void ApplyRailLayout(FlowLayoutPanel flow, IReadOnlyList<Control> cards)
        {
            if (cards.Count == 0)
            {
                return;
            }

            LoadoutRailLayout layout = GetRailLayout(flow, cards.Count);
            for (int index = 0; index < cards.Count; index++)
            {
                Control control = cards[index];
                control.Width = layout.IconSize;
                control.Height = layout.IconSize;
                control.Margin = new Padding(
                    0,
                    0,
                    0,
                    layout.GetBottomGap(index, cards.Count)
                );

                if (!flow.Controls.Contains(control))
                {
                    flow.Controls.Add(control);
                }
            }
        }

        private LoadoutRailLayout GetLoadoutRailLayout(int visibleSlotCount)
        {
            return GetRailLayout(_itemFlow, visibleSlotCount);
        }

        private static LoadoutRailLayout GetRailLayout(FlowLayoutPanel flow, int visibleSlotCount)
        {
            int slotCount = Math.Max(1, visibleSlotCount);
            int availableWidth = Math.Max(1, flow.ClientSize.Width - 2);
            int availableHeight = Math.Max(1, flow.ClientSize.Height);
            int maxIconSize = Math.Clamp(availableWidth, 34, 84);
            int desiredGap = slotCount > 1 ? 6 : 0;
            int fitByHeight = slotCount == 1
                ? availableHeight
                : (availableHeight - desiredGap * (slotCount - 1)) / slotCount;
            int iconSize = Math.Clamp(Math.Min(maxIconSize, fitByHeight), 28, maxIconSize);
            int baseBottomGap = slotCount <= 1 ? 0 : desiredGap;

            return new LoadoutRailLayout(iconSize, baseBottomGap, 0);
        }

        private bool CanAppendExtraRailSlot(int requestedSlotCount)
        {
            LoadoutRailLayout layout = GetLoadoutRailLayout(requestedSlotCount);
            int minimumUsefulIconSize = Math.Min(34, Math.Max(1, _itemFlow.ClientSize.Width - 2));
            return layout.IconSize >= minimumUsefulIconSize;
        }

        private void DisposeItemCards()
        {
            foreach (Control control in _itemFlow.Controls.Cast<Control>().ToArray())
            {
                control.Dispose();
            }

            _itemFlow.Controls.Clear();
            HideItemHoverOverlay();
        }

        private void DisposeSpellCards()
        {
            foreach (Control control in _spellFlow.Controls.Cast<Control>().ToArray())
            {
                control.Dispose();
            }

            _spellFlow.Controls.Clear();
            HideItemHoverOverlay();
        }

        private void DisposePortrait()
        {
            Image? oldImage = _portrait.Image;
            _portrait.Image = null;
            oldImage?.Dispose();
        }

        private string? FindPortrait(int paramId)
        {
            string root = Path.Combine(AppContext.BaseDirectory, "assets", "Characters");
            if (!Directory.Exists(root))
            {
                return null;
            }

            string fileName = $"{paramId}.png";
            return Directory
                .EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
        }

        private static string ToDisplayName(string value)
        {
            string cleaned = value
                .Replace('-', ' ')
                .Replace('_', ' ')
                .Trim();

            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleaned.ToLowerInvariant());
        }

        private static (string Name, string Qualifier) SplitEnemyDisplayName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return ("UNKNOWN TARGET", string.Empty);
            }

            string name = value.Trim();
            int open = name.IndexOf('(');
            int close = name.LastIndexOf(')');
            if (open <= 0 || close <= open)
            {
                return (name, string.Empty);
            }

            string baseName = name[..open].Trim();
            string qualifier = name[(open + 1)..close].Trim();
            return (
                string.IsNullOrWhiteSpace(baseName) ? name : baseName,
                qualifier
            );
        }

        private static Image? LoadImageUnlocked(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                using FileStream stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite
                );
                using Image source = Image.FromStream(stream);
                return new Bitmap(source);
            }
            catch
            {
                return null;
            }
        }

        private HashSet<int> LoadPreviouslyLoggedParamIds()
        {
            HashSet<int> ids = new();
            if (!File.Exists(_discoveredIdsPath))
            {
                return ids;
            }

            try
            {
                foreach (string line in File.ReadLines(_discoveredIdsPath))
                {
                    const string prefix = "Param ID: ";
                    if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string value = line[prefix.Length..].Trim();
                    int separator = value.IndexOf(' ');
                    if (separator >= 0)
                    {
                        value = value[..separator];
                    }

                    if (int.TryParse(value, out int id))
                    {
                        ids.Add(id);
                    }
                }
            }
            catch
            {
                // Logging problems must never stop the reader.
            }

            return ids;
        }

        private void LogParamIdIfNew(int paramId)
        {
            if (paramId <= 0 || !_loggedParamIds.Add(paramId))
            {
                return;
            }

            try
            {
                File.AppendAllText(
                    _discoveredIdsPath,
                    $"Param ID: {paramId}    First seen: {DateTime.Now:yyyy-MM-dd HH:mm:ss}" +
                    Environment.NewLine
                );
            }
            catch
            {
                // Discovery logging is optional and should never crash the live reader.
            }
        }

        private void LogEquipmentIfNew(CharacterInfo target)
        {
            if (target.Equipment.Count == 0)
            {
                return;
            }

            string signature = BuildLoadoutSignature(target);
            if (!_loggedEquipmentSignatures.Add($"{target.ParamId}:{signature}"))
            {
                return;
            }

            try
            {
                List<string> lines = new()
                {
                    $"Param ID: {target.ParamId}    First seen: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"CharaInitParam ID: {(target.CharaInitParamId > 0 ? target.CharaInitParamId.ToString(CultureInfo.InvariantCulture) : "-")}",
                    $"CharaInit source: {target.CharaInitParamSource}",
                    $"CharaInit candidate: {(target.CharaInitCandidateParamId > 0 ? target.CharaInitCandidateParamId.ToString(CultureInfo.InvariantCulture) : "-")}    Source: {target.CharaInitCandidateSource}",
                    $"Target address: 0x{target.Address:X}",
                    "Equipment:"
                };

                lines.AddRange(target.Equipment.Select(item =>
                    $"  {item.Slot}: {item.ItemId}"
                ));
                lines.Add(string.Empty);

                File.AppendAllLines(_discoveredEquipmentPath, lines);
            }
            catch
            {
                // Discovery logging is optional and should never crash the live reader.
            }
        }

        private void LogEquipmentDiagnosticsIfNeeded(
            MemoryReader memory,
            CharacterInfo target,
            ulong worldChrMan)
        {
            string key = $"{target.ParamId}:{target.LocalId}:0x{target.Address:X}";
            if (!_loggedEquipmentDiagnostics.Add(key))
            {
                return;
            }

            try
            {
                List<(string Label, ulong Base)> candidates = BuildEquipmentDiagnosticCandidates(
                    memory,
                    target,
                    worldChrMan
                );

                List<string> lines = new()
                {
                    $"Param ID: {target.ParamId}    Local ID: {target.LocalId}    Seen: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"CharaInitParam ID: {(target.CharaInitParamId > 0 ? target.CharaInitParamId.ToString(CultureInfo.InvariantCulture) : "-")}",
                    $"CharaInit source: {target.CharaInitParamSource}",
                    $"CharaInit candidate: {(target.CharaInitCandidateParamId > 0 ? target.CharaInitCandidateParamId.ToString(CultureInfo.InvariantCulture) : "-")}    Source: {target.CharaInitCandidateSource}",
                    $"Target address: 0x{target.Address:X}",
                    $"WorldChrMan: 0x{worldChrMan:X}",
                    "Candidate equipment blocks:"
                };

                foreach ((string label, ulong baseAddress) in candidates)
                {
                    lines.Add($"  {label}: 0x{baseAddress:X}");
                    if (!memory.IsLikelyPointer(baseAddress))
                    {
                        lines.Add("    not a readable pointer");
                        continue;
                    }

                    foreach ((string slot, ulong offset) in EquipmentItemProbe.Slots)
                    {
                        string value = ReadDiagnosticInt32(memory, baseAddress + offset);
                        lines.Add($"    +0x{offset:X3} {slot}: {value}");
                    }
                }

                List<EquipmentScoutCluster> scoutClusters = BuildDeepEquipmentScout(
                    memory,
                    target,
                    worldChrMan,
                    candidates,
                    _itemCatalog
                );

                lines.Add("Deep equipment scout:");
                if (scoutClusters.Count == 0)
                {
                    lines.Add("  no clustered catalog item IDs found");
                }
                else
                {
                    foreach (EquipmentScoutCluster cluster in scoutClusters.Take(12))
                    {
                        lines.Add(
                            $"  {cluster.Source}: base 0x{cluster.BaseAddress:X}, window +0x{cluster.WindowOffset:X}, score {cluster.Score}, {cluster.Items.Count} item IDs"
                        );

                        foreach (EquipmentScoutItem item in cluster.Items.Take(14))
                        {
                            lines.Add(
                                $"    +0x{item.Offset:X4}: {item.ItemId} / 0x{item.ItemId:X8}  {item.Name} [{item.Category}]"
                            );
                        }
                    }
                }

                lines.Add(string.Empty);
                File.AppendAllLines(_equipmentDiagnosticsPath, lines);
            }
            catch
            {
                // Diagnostics are optional; never interrupt the live reader.
            }
        }

        private static List<(string Label, ulong Base)> BuildEquipmentDiagnosticCandidates(
            MemoryReader memory,
            CharacterInfo target,
            ulong worldChrMan)
        {
            List<(string Label, ulong Base)> candidates = new()
            {
                ("character + 0x580 pointer", ReadPointerOrZero(memory, target.Address + 0x580)),
                ("character direct", target.Address),
                ("character + 0x190 pointer", ReadPointerOrZero(memory, target.Address + 0x190)),
                ("WorldChrMan matching character + 0x580 pointer", FindWorldChrManEquipmentBase(memory, worldChrMan, target))
            };

            ulong characterData = ReadPointerOrZero(memory, target.Address + 0x190);
            if (memory.IsLikelyPointer(characterData))
            {
                candidates.Add(("characterData + 0x68 pointer", ReadPointerOrZero(memory, characterData + 0x68)));
                candidates.Add(("characterData + 0x70 pointer", ReadPointerOrZero(memory, characterData + 0x70)));
                candidates.Add(("characterData + 0x580 pointer", ReadPointerOrZero(memory, characterData + 0x580)));
            }

            return candidates
                .Where(candidate => candidate.Base != 0)
                .GroupBy(candidate => candidate.Base)
                .Select(group => group.First())
                .ToList();
        }

        private static List<EquipmentScoutCluster> BuildDeepEquipmentScout(
            MemoryReader memory,
            CharacterInfo target,
            ulong worldChrMan,
            IReadOnlyList<(string Label, ulong Base)> seedCandidates,
            GameItemCatalog itemCatalog)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<(string Label, ulong Base)> bases = BuildEquipmentScoutBases(
                memory,
                target,
                worldChrMan,
                seedCandidates
            );

            List<EquipmentScoutCluster> clusters = new();
            HashSet<string> clusterKeys = new(StringComparer.OrdinalIgnoreCase);

            foreach ((string label, ulong baseAddress) in bases)
            {
                if (stopwatch.ElapsedMilliseconds > 350)
                {
                    break;
                }

                if (!memory.IsLikelyPointer(baseAddress))
                {
                    continue;
                }

                List<EquipmentScoutItem> hits = ScanEquipmentScoutHits(
                    memory,
                    baseAddress,
                    itemCatalog,
                    stopwatch
                );

                if (hits.Count < 2)
                {
                    continue;
                }

                for (int index = 0; index < hits.Count; index++)
                {
                    ulong windowStart = hits[index].Offset & ~0x3FUL;
                    ulong windowEnd = windowStart + 0x300;
                    List<EquipmentScoutItem> windowItems = hits
                        .Where(hit => hit.Offset >= windowStart && hit.Offset <= windowEnd)
                        .GroupBy(hit => hit.ItemId)
                        .Select(group => group.First())
                        .OrderBy(hit => hit.Offset)
                        .ToList();

                    int equipmentCount = windowItems.Count(item => GetScoutItemScore(item.Category) >= 4);
                    if (windowItems.Count < 3 && equipmentCount < 2)
                    {
                        continue;
                    }

                    string key = $"{baseAddress:X}:{windowStart:X}";
                    if (!clusterKeys.Add(key))
                    {
                        continue;
                    }

                    int score = windowItems.Sum(item => GetScoutItemScore(item.Category));
                    clusters.Add(new EquipmentScoutCluster(
                        label,
                        baseAddress,
                        windowStart,
                        windowItems,
                        score
                    ));
                }
            }

            return clusters
                .OrderByDescending(cluster => cluster.Score)
                .ThenByDescending(cluster => cluster.Items.Count)
                .ThenBy(cluster => cluster.WindowOffset)
                .Take(24)
                .ToList();
        }

        private static List<(string Label, ulong Base)> BuildEquipmentScoutBases(
            MemoryReader memory,
            CharacterInfo target,
            ulong worldChrMan,
            IReadOnlyList<(string Label, ulong Base)> seedCandidates)
        {
            List<(string Label, ulong Base)> bases = new(seedCandidates);

            AddScoutBase(bases, "character direct scout", target.Address);

            ulong worldCharacter = FindWorldChrManCharacter(memory, worldChrMan, target.LocalId);
            AddScoutBase(bases, "WorldChrMan matching character scout", worldCharacter);

            ulong characterData = ReadPointerOrZero(memory, target.Address + 0x190);
            AddScoutBase(bases, "character + 0x190 pointer scout", characterData);

            AddPointerScoutBases(memory, bases, "character pointer field", target.Address, 0x300);
            if (memory.IsLikelyPointer(characterData))
            {
                AddPointerScoutBases(memory, bases, "characterData pointer field", characterData, 0x300);
            }

            if (memory.IsLikelyPointer(worldCharacter) && worldCharacter != target.Address)
            {
                AddPointerScoutBases(memory, bases, "WorldChrMan character pointer field", worldCharacter, 0x300);
            }

            return bases
                .Where(candidate => candidate.Base != 0)
                .GroupBy(candidate => candidate.Base)
                .Select(group => group.First())
                .Take(24)
                .ToList();
        }

        private static void AddPointerScoutBases(
            MemoryReader memory,
            List<(string Label, ulong Base)> bases,
            string label,
            ulong ownerBase,
            ulong length)
        {
            if (!memory.IsLikelyPointer(ownerBase))
            {
                return;
            }

            for (ulong offset = 0; offset <= length; offset += 8)
            {
                ulong pointer = ReadPointerOrZero(memory, ownerBase + offset);
                if (!memory.IsLikelyPointer(pointer))
                {
                    continue;
                }

                AddScoutBase(bases, $"{label} +0x{offset:X}", pointer);
            }
        }

        private static void AddScoutBase(
            List<(string Label, ulong Base)> bases,
            string label,
            ulong baseAddress)
        {
            if (baseAddress != 0 && bases.All(candidate => candidate.Base != baseAddress))
            {
                bases.Add((label, baseAddress));
            }
        }

        private static List<EquipmentScoutItem> ScanEquipmentScoutHits(
            MemoryReader memory,
            ulong baseAddress,
            GameItemCatalog itemCatalog,
            Stopwatch stopwatch)
        {
            List<EquipmentScoutItem> hits = new();

            for (ulong offset = 0; offset <= 0x800; offset += 4)
            {
                if (stopwatch.ElapsedMilliseconds > 350 || hits.Count >= 64)
                {
                    break;
                }

                int itemId;
                try
                {
                    itemId = memory.ReadInt32(baseAddress + offset);
                }
                catch
                {
                    continue;
                }

                if (!TryGetScoutItem(itemId, itemCatalog, out EquipmentScoutItem? item))
                {
                    continue;
                }

                hits.Add(item! with { Offset = offset });
            }

            return hits;
        }

        private static bool TryGetScoutItem(
            int itemId,
            GameItemCatalog itemCatalog,
            out EquipmentScoutItem? item)
        {
            item = null;
            if (!IsLikelyEquipmentItemId(itemId))
            {
                return false;
            }

            GameItemRecord? record = itemCatalog.Find(itemId);
            if (record == null || GetScoutItemScore(record.Category) <= 0)
            {
                return false;
            }

            item = new EquipmentScoutItem(
                0,
                itemId,
                string.IsNullOrWhiteSpace(record.Name) ? "Unknown Item" : record.Name,
                string.IsNullOrWhiteSpace(record.Category) ? "Unknown" : record.Category
            );

            return true;
        }

        private static int GetScoutItemScore(string category)
        {
            if (category.Equals("Weapons", StringComparison.OrdinalIgnoreCase) ||
                category.Equals("Armor", StringComparison.OrdinalIgnoreCase) ||
                category.Equals("Accessories", StringComparison.OrdinalIgnoreCase))
            {
                return 4;
            }

            if (category.Equals("Goods", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return 0;
        }

        private static string ReadDiagnosticInt32(MemoryReader memory, ulong address)
        {
            try
            {
                int value = memory.ReadInt32(address);
                string verdict = IsLikelyEquipmentItemId(value) ? "candidate" : "ignored";
                return $"{value} / 0x{value:X8} ({verdict})";
            }
            catch (Exception exception)
            {
                return $"read failed: {exception.GetType().Name}";
            }
        }

        private static bool StaggerEquals(StaggerInfo? left, StaggerInfo? right)
        {
            if (left == null || right == null)
            {
                return left == right;
            }

            return Math.Abs(left.Current - right.Current) < 0.01f &&
                   Math.Abs(left.Maximum - right.Maximum) < 0.01f;
        }

        private void UpdateStatus(string message, LinkState? state = null)
        {
            _lastStatusMessage = message;
            SafeUi(() =>
            {
                _statusLabel.Text = message;
                if (state != null)
                {
                    _statusOrb.State = state.Value;
                }
            });
        }

        private void SafeUi(Action action)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(action);
                }
                catch
                {
                    // Window may be closing.
                }
                return;
            }

            action();
        }

        private static bool IsEldenRingRunning()
        {
            Process[] processes = Process.GetProcessesByName(ProcessName);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }
    }

    internal class RoundedPanel : Panel
    {
        private int _cornerRadius = 20;
        private int _borderThickness = 1;
        private Color _borderColor = Color.FromArgb(60, 255, 255, 255);
        private bool _glassFill = true;

        public RoundedPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        public int CornerRadius
        {
            get => _cornerRadius;
            set { _cornerRadius = Math.Max(0, value); Invalidate(); UpdateRegion(); }
        }

        public int BorderThickness
        {
            get => _borderThickness;
            set { _borderThickness = Math.Max(0, value); Invalidate(); }
        }

        public Color BorderColor
        {
            get => _borderColor;
            set { _borderColor = value; Invalidate(); }
        }

        public bool GlassFill
        {
            get => _glassFill;
            set { _glassFill = value; Invalidate(); }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = ClientRectangle;
            rect.Width -= 1;
            rect.Height -= 1;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            using GraphicsPath path = CreateRoundedPath(rect, CornerRadius);
            if (!GlassFill)
            {
                using SolidBrush brush = new(BackColor);
                e.Graphics.FillPath(brush, path);
                return;
            }

            Color top = Blend(BackColor, Color.White, 0.10f);
            Color bottom = Blend(BackColor, Color.Black, 0.18f);
            using LinearGradientBrush fill = new(rect, top, bottom, LinearGradientMode.Vertical);
            e.Graphics.FillPath(fill, path);

            Rectangle highlight = new(rect.X + 1, rect.Y + 1, Math.Max(1, rect.Width - 2), Math.Max(1, rect.Height / 2));
            using GraphicsPath highlightPath = CreateRoundedPath(highlight, Math.Max(0, CornerRadius - 1));
            using LinearGradientBrush sheen = new(
                highlight,
                Color.FromArgb(34, Color.White),
                Color.FromArgb(0, Color.White),
                LinearGradientMode.Vertical);
            e.Graphics.FillPath(sheen, highlightPath);
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            UpdateRegion();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle rect = ClientRectangle;
            rect.Width -= 1;
            rect.Height -= 1;

            using GraphicsPath path = CreateRoundedPath(rect, CornerRadius);
            using Pen pen = new Pen(BorderColor, BorderThickness);
            e.Graphics.DrawPath(pen, path);
        }

        private void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0)
            {
                return;
            }

            using GraphicsPath path = CreateRoundedPath(new Rectangle(0, 0, Width, Height), CornerRadius);
            Region?.Dispose();
            Region = new Region(path);
        }

        internal static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = Math.Min(Math.Min(radius * 2, rect.Width), rect.Height);
            if (diameter <= 1)
            {
                path.AddRectangle(rect);
                path.CloseFigure();
                return path;
            }

            Rectangle arc = new Rectangle(rect.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color Blend(Color baseColor, Color overlay, float amount)
        {
            amount = Math.Clamp(amount, 0f, 1f);
            int r = (int)Math.Round(baseColor.R + (overlay.R - baseColor.R) * amount);
            int g = (int)Math.Round(baseColor.G + (overlay.G - baseColor.G) * amount);
            int b = (int)Math.Round(baseColor.B + (overlay.B - baseColor.B) * amount);
            return Color.FromArgb(baseColor.A, r, g, b);
        }
    }

    internal sealed class VerticalFitPictureBox : PictureBox
    {
        public VerticalFitPictureBox()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            pe.Graphics.Clear(BackColor);

            if (Image == null || Width <= 0 || Height <= 0)
            {
                return;
            }

            pe.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            pe.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            float scale = Math.Max(
                (float)Width / Image.Width,
                (float)Height / Image.Height);
            int width = Math.Max(1, (int)Math.Round(Image.Width * scale));
            int height = Math.Max(1, (int)Math.Round(Image.Height * scale));
            Rectangle destination = new(
                (Width - width) / 2,
                (Height - height) / 2,
                width,
                height
            );

            pe.Graphics.DrawImage(Image, destination);
        }
    }

    internal sealed class StatusOrbControl : Control
    {
        private LinkState _state;

        public StatusOrbControl()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        public LinkState State
        {
            get => _state;
            set
            {
                if (_state == value)
                {
                    return;
                }

                _state = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            int size = Math.Max(10, Math.Min(Width, Height) - 6);
            Rectangle orb = new Rectangle(
                (Width - size) / 2,
                (Height - size) / 2,
                size,
                size
            );

            Color baseColor = State switch
            {
                LinkState.Linked => Color.FromArgb(72, 232, 142),
                LinkState.Linking => Color.FromArgb(246, 211, 84),
                _ => Color.FromArgb(78, 78, 84)
            };

            using GraphicsPath shadowPath = new();
            shadowPath.AddEllipse(orb);
            using PathGradientBrush glow = new(shadowPath)
            {
                CenterColor = Color.FromArgb(State == LinkState.Offline ? 55 : 115, baseColor),
                SurroundColors = new[] { Color.Transparent }
            };
            Rectangle glowRect = Rectangle.Inflate(orb, 8, 8);
            e.Graphics.FillEllipse(glow, glowRect);

            using LinearGradientBrush fill = new(
                orb,
                ControlPaint.Light(baseColor, 0.35f),
                ControlPaint.Dark(baseColor, 0.35f),
                LinearGradientMode.ForwardDiagonal
            );
            e.Graphics.FillEllipse(fill, orb);

            Rectangle shine = new Rectangle(
                orb.X + Math.Max(2, orb.Width / 5),
                orb.Y + Math.Max(2, orb.Height / 6),
                Math.Max(4, orb.Width / 3),
                Math.Max(3, orb.Height / 4)
            );
            using SolidBrush shineBrush = new(Color.FromArgb(150, Color.White));
            e.Graphics.FillEllipse(shineBrush, shine);

            using Pen border = new(Color.FromArgb(135, 255, 255, 255), 1f);
            e.Graphics.DrawEllipse(border, orb);
        }
    }

    internal sealed class DrawerTabButton : Control
    {
        private bool _expanded;
        private bool _hovered;
        private bool _pressed;

        public DrawerTabButton()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.Selectable, true);
        }

        public bool Expanded
        {
            get => _expanded;
            set
            {
                if (_expanded == value)
                {
                    return;
                }

                _expanded = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _pressed = true;
                Invalidate();
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = ClientRectangle;
            rect.Width -= 1;
            rect.Height -= 1;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            Color top = _pressed
                ? Color.FromArgb(68, 68, 74)
                : _hovered
                    ? Color.FromArgb(72, 72, 78)
                    : Color.FromArgb(60, 60, 66);
            Color bottom = _pressed
                ? Color.FromArgb(35, 35, 40)
                : Color.FromArgb(38, 38, 43);

            using GraphicsPath path = RoundedPanel.CreateRoundedPath(rect, Expanded ? 14 : 19);
            using LinearGradientBrush fill = new(rect, top, bottom, LinearGradientMode.Vertical);
            e.Graphics.FillPath(fill, path);
            using Pen border = new(Color.FromArgb(118, 255, 255, 255), 1f);
            e.Graphics.DrawPath(border, path);

            string text = string.IsNullOrWhiteSpace(Text) ? "ITEMS" : Text;
            using Font font = new(Font.FontFamily, Expanded ? 8f : 11f, FontStyle.Bold);
            using SolidBrush brush = new(ForeColor);
            StringFormat format = new()
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            if (Expanded)
            {
                e.Graphics.DrawString(text, font, brush, rect, format);
                return;
            }

            e.Graphics.TranslateTransform(rect.Left + rect.Width / 2f, rect.Top + rect.Height / 2f);
            e.Graphics.RotateTransform(-90f);
            RectangleF textRect = new(
                -rect.Height / 2f,
                -rect.Width / 2f,
                rect.Height,
                rect.Width);
            e.Graphics.DrawString(text, font, brush, textRect, format);
            e.Graphics.ResetTransform();
        }
    }

    internal sealed class WindowChromeButton : Control
    {
        private readonly bool _destructive;
        private bool _hovered;
        private bool _pressed;
        private string _glyph;

        public WindowChromeButton(string glyph, bool destructive = false)
        {
            _glyph = glyph;
            _destructive = destructive;
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            DoubleBuffered = true;
            ResizeRedraw = true;
            Cursor = Cursors.Hand;
            ForeColor = Color.FromArgb(232, 232, 236);
            BackColor = Color.Transparent;
        }

        public void SetGlyph(string glyph)
        {
            if (string.Equals(_glyph, glyph, StringComparison.Ordinal))
            {
                return;
            }

            _glyph = glyph;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _pressed = true;
                Invalidate();
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = ClientRectangle;
            rect.Width -= 1;
            rect.Height -= 1;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            Color top;
            Color bottom;
            Color border;
            if (_destructive && _hovered)
            {
                top = _pressed ? Color.FromArgb(170, 52, 52) : Color.FromArgb(126, 47, 48);
                bottom = _pressed ? Color.FromArgb(98, 30, 32) : Color.FromArgb(74, 28, 30);
                border = Color.FromArgb(150, 255, 190, 190);
            }
            else
            {
                top = _pressed
                    ? Color.FromArgb(62, 62, 68)
                    : _hovered
                        ? Color.FromArgb(56, 56, 62)
                        : Color.FromArgb(34, 34, 39);
                bottom = _pressed
                    ? Color.FromArgb(32, 32, 37)
                    : Color.FromArgb(24, 24, 28);
                border = _hovered
                    ? Color.FromArgb(128, 255, 255, 255)
                    : Color.FromArgb(70, 255, 255, 255);
            }

            using GraphicsPath path = RoundedPanel.CreateRoundedPath(rect, 9);
            using LinearGradientBrush fill = new(rect, top, bottom, LinearGradientMode.Vertical);
            e.Graphics.FillPath(fill, path);
            using Pen pen = new(border, 1f);
            e.Graphics.DrawPath(pen, path);

            TextRenderer.DrawText(
                e.Graphics,
                _glyph,
                new Font("Segoe UI Semibold", 9.5f),
                rect,
                Color.FromArgb(238, 238, 242),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class FreecamActionButton : Control
    {
        private bool _hovered;
        private bool _pressed;

        public FreecamActionButton(string text)
        {
            Text = text;
            DoubleBuffered = true;
            ResizeRedraw = true;
            Cursor = Cursors.Hand;
            ForeColor = Color.FromArgb(236, 236, 240);
            Font = new Font("Segoe UI Semibold", 8.5f);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _pressed = true;
                Invalidate();
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = ClientRectangle;
            rect.Width -= 1;
            rect.Height -= 1;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            Color top = _pressed
                ? Color.FromArgb(55, 55, 61)
                : _hovered
                    ? Color.FromArgb(50, 50, 56)
                    : Color.FromArgb(36, 36, 41);
            Color bottom = _pressed
                ? Color.FromArgb(24, 24, 29)
                : Color.FromArgb(22, 22, 27);
            Color border = _hovered
                ? Color.FromArgb(118, 255, 255, 255)
                : Color.FromArgb(76, 255, 255, 255);

            using GraphicsPath path = RoundedPanel.CreateRoundedPath(rect, 8);
            using LinearGradientBrush fill = new(rect, top, bottom, LinearGradientMode.Vertical);
            e.Graphics.FillPath(fill, path);
            using Pen pen = new(border, 1f);
            e.Graphics.DrawPath(pen, path);

            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                rect,
                ForeColor,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);
        }
    }

    internal sealed class VitalBar : Control
    {
        private readonly System.Windows.Forms.Timer _flashTimer;
        private int _value;
        private bool _flashing;
        private bool _flashVisible;
        private DateTime _flashUntilUtc;
        private Color _flashFillColor = Color.White;

        public VitalBar()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            _flashTimer = new System.Windows.Forms.Timer { Interval = 110 };
            _flashTimer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow >= _flashUntilUtc)
                {
                    _flashTimer.Stop();
                    _flashing = false;
                    _flashVisible = false;
                    Invalidate();
                    return;
                }

                _flashVisible = !_flashVisible;
                Invalidate();
            };
        }

        public int Maximum { get; set; } = 100;
        public Color TrackColor { get; set; } = Color.FromArgb(40, 40, 45);
        public Color FillColor { get; set; } = Color.WhiteSmoke;
        public Color WarningFillColor { get; set; } = Color.Gainsboro;
        public Color CriticalFillColor { get; set; } = Color.Gray;
        public int CornerRadius { get; set; } = 8;
        public bool ShowValueText { get; set; }

        public int Value
        {
            get => _value;
            set
            {
                int clamped = Math.Clamp(value, 0, Math.Max(1, Maximum));
                if (_value == clamped)
                {
                    return;
                }

                _value = clamped;
                Invalidate();
            }
        }

        public void Flash()
        {
            Flash(Color.White, 110);
        }

        public void Flash(Color color, int durationMs)
        {
            _flashing = true;
            _flashVisible = true;
            _flashUntilUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(1, durationMs));
            _flashFillColor = color;
            _flashTimer.Stop();
            _flashTimer.Interval = Math.Min(180, Math.Max(1, durationMs));
            _flashTimer.Start();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle track = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));

            using GraphicsPath trackPath = RoundedPanel.CreateRoundedPath(track, CornerRadius);
            using LinearGradientBrush trackBrush = new(
                track,
                ControlPaint.Light(TrackColor, 0.12f),
                ControlPaint.Dark(TrackColor, 0.20f),
                LinearGradientMode.Vertical);
            e.Graphics.FillPath(trackBrush, trackPath);

            float ratio = Maximum <= 0 ? 0f : (float)Value / Maximum;
            int fillWidth = (int)Math.Round(track.Width * ratio);
            bool showFlash = _flashing && _flashVisible;
            if (showFlash && fillWidth <= 0)
            {
                fillWidth = track.Width;
            }

            if (fillWidth > 0)
            {
                Rectangle fillRect = new Rectangle(track.X, track.Y, Math.Max(1, fillWidth), track.Height);
                Color fill = showFlash
                    ? _flashFillColor
                    : ratio <= 0.25f
                        ? CriticalFillColor
                        : ratio <= 0.60f
                            ? WarningFillColor
                            : FillColor;

                using GraphicsPath fillPath = RoundedPanel.CreateRoundedPath(fillRect, CornerRadius);
                using LinearGradientBrush fillBrush = new(
                    fillRect,
                    ControlPaint.Light(fill, 0.22f),
                    ControlPaint.Dark(fill, 0.14f),
                    LinearGradientMode.Vertical);
                e.Graphics.FillPath(fillBrush, fillPath);

                Rectangle sheen = new(fillRect.X + 1, fillRect.Y + 1, Math.Max(1, fillRect.Width - 2), Math.Max(1, fillRect.Height / 2));
                using GraphicsPath sheenPath = RoundedPanel.CreateRoundedPath(sheen, Math.Max(0, CornerRadius - 1));
                using SolidBrush sheenBrush = new(Color.FromArgb(46, Color.White));
                e.Graphics.FillPath(sheenBrush, sheenPath);
            }

            using Pen border = new Pen(Color.FromArgb(120, 255, 255, 255), 1f);
            e.Graphics.DrawPath(border, trackPath);

            if (ShowValueText)
            {
                string text = string.IsNullOrWhiteSpace(Text)
                    ? $"{Value}%"
                    : Text;
                TextRenderer.DrawText(
                    e.Graphics,
                    text,
                    new Font("Segoe UI Semibold", 8.5f),
                    track,
                    ratio > 0.52f ? Color.FromArgb(22, 22, 24) : Color.FromArgb(230, 230, 233),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                );
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _flashTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class ItemInfo
    {
        public long ItemId { get; set; }
        public string Slot { get; set; } = "Equipped";
        public string Name { get; set; } = "Unknown Item";
        public string Description { get; set; } = "Description unavailable.";
        public string? Effect { get; set; }
        public string? ImagePath { get; set; }
        public string FolderPath { get; set; } = string.Empty;
        public string Category { get; set; } = "Unknown";
        public int Quantity { get; set; } = 1;

        public string DisplayDescription
        {
            get
            {
                string description = IsPlaceholderDescription(Description)
                    ? string.Empty
                    : Description.Trim();
                string effect = string.IsNullOrWhiteSpace(Effect)
                    ? string.Empty
                    : Effect.Trim();

                if (string.IsNullOrWhiteSpace(effect))
                {
                    return string.IsNullOrWhiteSpace(description)
                        ? "Description unavailable."
                        : description;
                }

                if (!string.IsNullOrWhiteSpace(description)
                    && description.Contains(effect, StringComparison.OrdinalIgnoreCase))
                {
                    return description;
                }

                return string.IsNullOrWhiteSpace(description)
                    ? effect
                    : $"{effect}{Environment.NewLine}{Environment.NewLine}{description}";
            }
        }

        private static bool IsPlaceholderDescription(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            string normalized = value.Trim();
            return normalized.Equals("Description unavailable.", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Description pending extraction.", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Pending extraction.", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class ItemHistoryEntry
    {
        public long ItemId { get; set; }
        public string Name { get; set; } = "Unknown Item";
        public string Category { get; set; } = "Unknown";
        public int Quantity { get; set; } = 1;
        public string Source { get; set; } = "Unknown";
        public int InventoryIndex { get; set; } = -1;
        public DateTimeOffset PickedUpAt { get; set; } = DateTimeOffset.Now;
    }

    internal sealed record InventoryItemSnapshot(
        int InventoryIndex,
        long ItemId,
        int Quantity,
        string Name,
        string Category
    );

    internal sealed record TargetHandleInfo(
        ulong PackedHandle,
        uint LocalId,
        int Area,
        string Source)
    {
        public static readonly TargetHandleInfo None = new(0, 0, 0, "not resolved");
    }

    internal sealed class GameItemRecord
    {
        public long ItemId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = "Unknown";
        public string? Description { get; set; }
        public string? Effect { get; set; }
        public int? IconId { get; set; }
        public string? ImagePath { get; set; }
    }

    internal sealed class EnemyRecord
    {
        public int ParamId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int BehaviorVariationId { get; set; }
        public int NormalChangeTexChrId { get; set; }
        public int Hp { get; set; }
        public int Stamina { get; set; }
        public int ItemLotIdEnemy { get; set; }
    }

    internal sealed class EnemyDatabase
    {
        public int Version { get; set; }
        public string? GeneratedAt { get; set; }
        public string? Identity { get; set; }
        public string? Note { get; set; }
        public EnemyRecord[] Entries { get; set; } = Array.Empty<EnemyRecord>();
    }

    internal sealed class EnemyCatalog
    {
        private readonly Dictionary<int, EnemyRecord> _enemies;

        private EnemyCatalog(Dictionary<int, EnemyRecord> enemies)
        {
            _enemies = enemies;
        }

        public static EnemyCatalog Load()
        {
            string path = Path.Combine(
                AppContext.BaseDirectory,
                "assets",
                "Database",
                "enemies.json"
            );

            Dictionary<int, EnemyRecord> enemies = new();
            if (!File.Exists(path))
            {
                return new EnemyCatalog(enemies);
            }

            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                EnemyDatabase? database = JsonSerializer.Deserialize<EnemyDatabase>(
                    File.ReadAllText(path),
                    options
                );

                if (database?.Entries == null)
                {
                    return new EnemyCatalog(enemies);
                }

                foreach (EnemyRecord entry in database.Entries)
                {
                    if (entry.ParamId <= 0 || string.IsNullOrWhiteSpace(entry.Name))
                    {
                        continue;
                    }

                    enemies[entry.ParamId] = entry;
                }
            }
            catch
            {
                // Enemy names improve display only; live target reading should continue without them.
            }

            return new EnemyCatalog(enemies);
        }

        public string? FindName(int paramId)
        {
            return _enemies.TryGetValue(paramId, out EnemyRecord? record)
                ? record.Name
                : null;
        }

        public EnemyRecord? Find(int paramId)
        {
            return _enemies.TryGetValue(paramId, out EnemyRecord? record)
                ? record
                : null;
        }
    }

    internal sealed class EnemyLoadoutSlot
    {
        public string Slot { get; set; } = "Equipped";
        public long ItemId { get; set; }
        public string? Note { get; set; }
    }

    internal sealed class EnemyLoadoutEntry
    {
        public int ParamId { get; set; }
        public string? Name { get; set; }
        public string? Source { get; set; }
        public bool Verified { get; set; }
        public EnemyLoadoutSlot[] Slots { get; set; } = Array.Empty<EnemyLoadoutSlot>();
    }

    internal sealed class EnemyLoadoutDatabase
    {
        public int Version { get; set; }
        public string? GeneratedAt { get; set; }
        public string? Policy { get; set; }
        public EnemyLoadoutEntry[] Entries { get; set; } = Array.Empty<EnemyLoadoutEntry>();
    }

    internal sealed class EnemyLoadoutCatalog
    {
        private readonly Dictionary<int, IReadOnlyList<EnemyLoadoutSlot>> _loadouts;

        private EnemyLoadoutCatalog(Dictionary<int, IReadOnlyList<EnemyLoadoutSlot>> loadouts)
        {
            _loadouts = loadouts;
        }

        public static EnemyLoadoutCatalog Load(string fileName = "enemy_loadouts.json")
        {
            string path = Path.Combine(
                AppContext.BaseDirectory,
                "assets",
                "Database",
                fileName
            );

            Dictionary<int, IReadOnlyList<EnemyLoadoutSlot>> loadouts = new();
            if (!File.Exists(path))
            {
                return new EnemyLoadoutCatalog(loadouts);
            }

            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                EnemyLoadoutDatabase? database = JsonSerializer.Deserialize<EnemyLoadoutDatabase>(
                    File.ReadAllText(path),
                    options
                );

                if (database?.Entries == null)
                {
                    return new EnemyLoadoutCatalog(loadouts);
                }

                foreach (EnemyLoadoutEntry entry in database.Entries)
                {
                    if (entry.ParamId <= 0 || !entry.Verified || entry.Slots.Length == 0)
                    {
                        continue;
                    }

                    EnemyLoadoutSlot[] slots = entry.Slots
                        .Where(slot => slot.ItemId > 0)
                        .ToArray();
                    if (slots.Length > 0)
                    {
                        loadouts[entry.ParamId] = slots;
                    }
                }
            }
            catch
            {
                // Verified loadout data is optional; the live reader should continue without it.
            }

            return new EnemyLoadoutCatalog(loadouts);
        }

        public IReadOnlyList<EnemyLoadoutSlot> Find(int paramId)
        {
            return _loadouts.TryGetValue(paramId, out IReadOnlyList<EnemyLoadoutSlot>? slots)
                ? slots
                : Array.Empty<EnemyLoadoutSlot>();
        }
    }

    internal sealed class EnemyMagicCatalog
    {
        private readonly Dictionary<int, IReadOnlyList<EnemyLoadoutSlot>> _abilities;

        private EnemyMagicCatalog(Dictionary<int, IReadOnlyList<EnemyLoadoutSlot>> abilities)
        {
            _abilities = abilities;
        }

        public static EnemyMagicCatalog Load()
        {
            string path = Path.Combine(
                AppContext.BaseDirectory,
                "assets",
                "Database",
                "enemy_magic.json"
            );

            Dictionary<int, IReadOnlyList<EnemyLoadoutSlot>> abilities = new();
            if (!File.Exists(path))
            {
                return new EnemyMagicCatalog(abilities);
            }

            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                EnemyLoadoutDatabase? database = JsonSerializer.Deserialize<EnemyLoadoutDatabase>(
                    File.ReadAllText(path),
                    options
                );

                if (database?.Entries == null)
                {
                    return new EnemyMagicCatalog(abilities);
                }

                foreach (EnemyLoadoutEntry entry in database.Entries)
                {
                    if (entry.ParamId <= 0 || !entry.Verified || entry.Slots.Length == 0)
                    {
                        continue;
                    }

                    EnemyLoadoutSlot[] slots = entry.Slots
                        .Where(slot => slot.ItemId > 0)
                        .ToArray();
                    if (slots.Length > 0)
                    {
                        abilities[entry.ParamId] = slots;
                    }
                }
            }
            catch
            {
                // Behavior-derived magic data is optional; live target reading should continue without it.
            }

            return new EnemyMagicCatalog(abilities);
        }

        public IReadOnlyList<EnemyLoadoutSlot> Find(int paramId)
        {
            return _abilities.TryGetValue(paramId, out IReadOnlyList<EnemyLoadoutSlot>? abilities)
                ? abilities
                : Array.Empty<EnemyLoadoutSlot>();
        }
    }

    internal sealed class EnemyDropRecord
    {
        public int Slot { get; set; }
        public int LotRowId { get; set; }
        public int ItemSlot { get; set; }
        public long ItemId { get; set; }
        public string ItemName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public double ChancePercent { get; set; }
        public int BasePoint { get; set; }
        public int TotalBasePoint { get; set; }
    }

    internal sealed class EnemyRewardRecord
    {
        public int ParamId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Runes { get; set; }
        public int ItemLotIdEnemy { get; set; }
        public string? ItemLotName { get; set; }
        [JsonConverter(typeof(SingleOrArrayJsonConverter<EnemyDropRecord>))]
        public EnemyDropRecord[] Drops { get; set; } = Array.Empty<EnemyDropRecord>();
    }

    internal sealed class EnemyRewardDatabase
    {
        public int Version { get; set; }
        public string? GeneratedAt { get; set; }
        public string? Identity { get; set; }
        public string? Source { get; set; }
        public EnemyRewardRecord[] Entries { get; set; } = Array.Empty<EnemyRewardRecord>();
    }

    internal sealed class EnemyRewardCatalog
    {
        private readonly Dictionary<int, EnemyRewardRecord> _records;

        private EnemyRewardCatalog(Dictionary<int, EnemyRewardRecord> records)
        {
            _records = records;
        }

        public static EnemyRewardCatalog Load()
        {
            string path = Path.Combine(
                AppContext.BaseDirectory,
                "assets",
                "Database",
                "enemy_rewards.json"
            );

            Dictionary<int, EnemyRewardRecord> records = new();
            if (!File.Exists(path))
            {
                return new EnemyRewardCatalog(records);
            }

            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                EnemyRewardDatabase? database = JsonSerializer.Deserialize<EnemyRewardDatabase>(
                    File.ReadAllText(path),
                    options
                );

                if (database?.Entries == null)
                {
                    return new EnemyRewardCatalog(records);
                }

                foreach (EnemyRewardRecord entry in database.Entries)
                {
                    if (entry.ParamId <= 0)
                    {
                        continue;
                    }

                    entry.Drops ??= Array.Empty<EnemyDropRecord>();
                    records[entry.ParamId] = entry;
                }
            }
            catch
            {
                // Reward intel is additive; target display should continue if the cache is absent.
            }

            return new EnemyRewardCatalog(records);
        }

        public EnemyRewardRecord? Find(int paramId)
        {
            return _records.TryGetValue(paramId, out EnemyRewardRecord? record)
                ? record
                : null;
        }
    }

    internal sealed record RuneReward(int ClearCount, long Runes, double Multiplier);

    internal sealed record LoadoutRailLayout(int IconSize, int BaseBottomGap, int ExtraGapCount)
    {
        public int GetBottomGap(int index, int slotCount)
        {
            if (index >= slotCount - 1)
            {
                return 0;
            }

            return BaseBottomGap + (index < ExtraGapCount ? 1 : 0);
        }
    }

    internal sealed class RuneScalingEntry
    {
        public int ParamId { get; set; }
        public int BaseRunes { get; set; }
        public int GameClearSpEffectId { get; set; }
        public double GameClearHaveSoulRate { get; set; }
    }

    internal sealed class RuneScalingDatabase
    {
        public int Version { get; set; }
        public string? GeneratedAt { get; set; }
        public string? Note { get; set; }
        public double[] ClearCountCorrections { get; set; } = Array.Empty<double>();
        public RuneScalingEntry[] Entries { get; set; } = Array.Empty<RuneScalingEntry>();
    }

    internal sealed class RuneScalingCatalog
    {
        private static readonly double[] FallbackClearCountCorrections =
        {
            1.0,
            1.0,
            1.10,
            1.125,
            1.20,
            1.225,
            1.25,
            1.275,
            1.275
        };

        private readonly Dictionary<int, RuneScalingEntry> _entries;
        private readonly double[] _clearCountCorrections;

        private RuneScalingCatalog(
            Dictionary<int, RuneScalingEntry> entries,
            double[] clearCountCorrections)
        {
            _entries = entries;
            _clearCountCorrections = clearCountCorrections.Length == 0
                ? FallbackClearCountCorrections
                : clearCountCorrections;
        }

        public static RuneScalingCatalog Load()
        {
            string path = Path.Combine(
                AppContext.BaseDirectory,
                "assets",
                "Database",
                "rune_scaling.json"
            );

            Dictionary<int, RuneScalingEntry> entries = new();
            double[] clearCountCorrections = FallbackClearCountCorrections;

            if (!File.Exists(path))
            {
                return new RuneScalingCatalog(entries, clearCountCorrections);
            }

            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                RuneScalingDatabase? database = JsonSerializer.Deserialize<RuneScalingDatabase>(
                    File.ReadAllText(path),
                    options
                );

                if (database?.ClearCountCorrections?.Length > 0)
                {
                    clearCountCorrections = database.ClearCountCorrections;
                }

                if (database?.Entries != null)
                {
                    foreach (RuneScalingEntry entry in database.Entries)
                    {
                        if (entry.ParamId <= 0)
                        {
                            continue;
                        }

                        entries[entry.ParamId] = entry;
                    }
                }
            }
            catch
            {
                // Rune math should fall back cleanly if the generated cache is absent or stale.
            }

            return new RuneScalingCatalog(entries, clearCountCorrections);
        }

        public RuneReward Calculate(int paramId, int baseRunes, int clearCount)
        {
            int clampedClearCount = Math.Clamp(clearCount, 0, _clearCountCorrections.Length - 1);
            if (clampedClearCount == 0)
            {
                return new RuneReward(0, baseRunes, 1.0);
            }

            double journeyCorrection = _clearCountCorrections[clampedClearCount];
            double lapMultiplier = 1.0;

            if (_entries.TryGetValue(paramId, out RuneScalingEntry? entry) &&
                entry.GameClearSpEffectId > 0 &&
                entry.GameClearHaveSoulRate > 0.0)
            {
                lapMultiplier = entry.GameClearHaveSoulRate;
            }

            double multiplier = lapMultiplier * journeyCorrection;
            long runes = (long)Math.Round(baseRunes * multiplier, MidpointRounding.AwayFromZero);
            return new RuneReward(clampedClearCount, runes, multiplier);
        }
    }

    internal sealed class SingleOrArrayJsonConverter<T> : JsonConverter<T[]>
    {
        public override T[] Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return Array.Empty<T>();
            }

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                return JsonSerializer.Deserialize<T[]>(ref reader, options) ?? Array.Empty<T>();
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                T? item = JsonSerializer.Deserialize<T>(ref reader, options);
                return item == null
                    ? Array.Empty<T>()
                    : new[] { item };
            }

            throw new JsonException($"Unexpected token {reader.TokenType} while reading array.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            T[] value,
            JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }

    internal sealed class CharaInitLoadoutSlot
    {
        public string Slot { get; set; } = "Equipped";
        public long ItemId { get; set; }
        public string Category { get; set; } = "Unknown";
        public int Quantity { get; set; } = 1;
        public string? Field { get; set; }
        public string? QuantityField { get; set; }
    }

    internal sealed class CharaInitLoadoutEntry
    {
        public int ParamId { get; set; }
        public string? Name { get; set; }
        public CharaInitLoadoutSlot[] Slots { get; set; } = Array.Empty<CharaInitLoadoutSlot>();
    }

    internal sealed class CharaInitLoadoutDatabase
    {
        public int Version { get; set; }
        public string? GeneratedAt { get; set; }
        public string? Source { get; set; }
        public CharaInitLoadoutEntry[] Entries { get; set; } = Array.Empty<CharaInitLoadoutEntry>();
    }

    internal sealed class CharaInitLoadoutCatalog
    {
        private readonly Dictionary<int, IReadOnlyList<CharaInitLoadoutSlot>> _loadouts;

        private CharaInitLoadoutCatalog(Dictionary<int, IReadOnlyList<CharaInitLoadoutSlot>> loadouts)
        {
            _loadouts = loadouts;
        }

        public static CharaInitLoadoutCatalog Load()
        {
            string path = Path.Combine(
                AppContext.BaseDirectory,
                "assets",
                "Database",
                "chara_init_loadouts.json"
            );

            Dictionary<int, IReadOnlyList<CharaInitLoadoutSlot>> loadouts = new();
            if (!File.Exists(path))
            {
                return new CharaInitLoadoutCatalog(loadouts);
            }

            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                CharaInitLoadoutDatabase? database = JsonSerializer.Deserialize<CharaInitLoadoutDatabase>(
                    File.ReadAllText(path),
                    options
                );

                if (database?.Entries == null)
                {
                    return new CharaInitLoadoutCatalog(loadouts);
                }

                foreach (CharaInitLoadoutEntry entry in database.Entries)
                {
                    if (entry.ParamId <= 0 || entry.Slots.Length == 0)
                    {
                        continue;
                    }

                    CharaInitLoadoutSlot[] slots = entry.Slots
                        .Where(slot => slot.ItemId > 0 && !string.IsNullOrWhiteSpace(slot.Slot))
                        .Select(slot => new CharaInitLoadoutSlot
                        {
                            Slot = slot.Slot,
                            ItemId = slot.ItemId,
                            Category = string.IsNullOrWhiteSpace(slot.Category) ? "Unknown" : slot.Category,
                            Quantity = Math.Max(1, slot.Quantity),
                            Field = slot.Field,
                            QuantityField = slot.QuantityField
                        })
                        .ToArray();

                    if (slots.Length > 0)
                    {
                        loadouts[entry.ParamId] = slots;
                    }
                }
            }
            catch
            {
                // Generated data is a convenience fallback; live/manual loadouts remain primary.
            }

            return new CharaInitLoadoutCatalog(loadouts);
        }

        public IReadOnlyList<CharaInitLoadoutSlot> Find(int paramId)
        {
            return _loadouts.TryGetValue(paramId, out IReadOnlyList<CharaInitLoadoutSlot>? slots)
                ? slots
                : Array.Empty<CharaInitLoadoutSlot>();
        }

        public bool Contains(int paramId)
        {
            return _loadouts.ContainsKey(paramId);
        }

        public IEnumerable<int> ParamIds => _loadouts.Keys;

        public bool IsLikelyLiveCandidate(int paramId)
        {
            return paramId >= 1000 && _loadouts.ContainsKey(paramId);
        }
    }

    internal sealed class GameItemCatalog
    {
        private readonly Dictionary<long, GameItemRecord> _items;
        private readonly Dictionary<string, GameItemRecord> _itemsByCategoryKey;

        private GameItemCatalog(
            Dictionary<long, GameItemRecord> items,
            Dictionary<string, GameItemRecord> itemsByCategoryKey)
        {
            _items = items;
            _itemsByCategoryKey = itemsByCategoryKey;
        }

        public static GameItemCatalog Load()
        {
            Dictionary<long, GameItemRecord> items = new();
            Dictionary<string, GameItemRecord> itemsByCategoryKey = new(StringComparer.OrdinalIgnoreCase);

            LoadCollectiveItemFile(
                Path.Combine(AppContext.BaseDirectory, "assets", "Database", "enemy_intel_collective.json"),
                items,
                itemsByCategoryKey
            );

            LoadDatabaseItemFile(
                Path.Combine(AppContext.BaseDirectory, "assets", "Database", "items.json"),
                items,
                itemsByCategoryKey
            );

            LoadEquipParamCsvIconMap(
                @"C:\Users\sethm\Downloads\New folder\EquipParamWeapon.csv",
                "Weapons",
                items
            );

            foreach ((string category, string fileName) in GetRowNameExports())
            {
                foreach (string basePath in GetCandidateBasePaths())
                {
                    string path = Path.Combine(
                        basePath,
                        fileName,
                        "Row Name Export",
                        $"{fileName}.txt"
                    );

                    LoadRowNameFile(path, category, items);
                }
            }

            foreach (string tablePath in GetCandidateCheatTables())
            {
                LoadCheatTableNameMap(tablePath, items);
            }

            return new GameItemCatalog(items, itemsByCategoryKey);
        }

        public GameItemRecord? Find(long itemId)
        {
            return _items.TryGetValue(itemId, out GameItemRecord? record)
                ? record
                : null;
        }

        public GameItemRecord? Find(long itemId, string? category)
        {
            if (!string.IsNullOrWhiteSpace(category) &&
                _itemsByCategoryKey.TryGetValue(BuildCategoryItemKey(category, itemId), out GameItemRecord? categoryRecord))
            {
                return categoryRecord;
            }

            return Find(itemId);
        }

        public GameItemRecord? FindByName(string name)
        {
            return _items.Values
                .Where(record => string.Equals(
                    record.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(record => record.ItemId >= 100_000_000 ? 1 : 0)
                .ThenBy(record => record.ItemId)
                .FirstOrDefault();
        }

        private static IEnumerable<(string Category, string FileName)> GetRowNameExports()
        {
            yield return ("Goods", "EquipParamGoods");
            yield return ("Weapons", "EquipParamWeapon");
            yield return ("Armor", "EquipParamProtector");
            yield return ("Accessories", "EquipParamAccessory");
        }

        private static IEnumerable<string> GetCandidateBasePaths()
        {
            string current = AppContext.BaseDirectory;

            for (int depth = 0; depth < 8; depth++)
            {
                string candidate = Path.Combine(current, "smithbox projects");
                if (Directory.Exists(candidate))
                {
                    yield return candidate;
                }

                DirectoryInfo? parent = Directory.GetParent(current);
                if (parent == null)
                {
                    break;
                }

                current = parent.FullName;
            }

            string localBuildPath = @"C:\Seths Build\smithbox projects";
            if (Directory.Exists(localBuildPath))
            {
                yield return localBuildPath;
            }
        }

        private static IEnumerable<string> GetCandidateCheatTables()
        {
            string tgaPath = @"D:\Cheat place\ELDEN RING CHARACTER INFO PUSH\ER_TGA_v1.18.0.CT";
            if (File.Exists(tgaPath))
            {
                yield return tgaPath;
            }

            string hexintonPath =
                @"C:\Users\sethm\Downloads\Hexinton All in One 6.1\eldenring_all-in-one_Hexinton-v6.1_ce7.5\eldenring_all-in-one_Hexinton-v6.1_ce7.5.ct.ct";
            if (File.Exists(hexintonPath))
            {
                yield return hexintonPath;
            }
        }

        private static void LoadDatabaseItemFile(
            string path,
            Dictionary<long, GameItemRecord> items,
            Dictionary<string, GameItemRecord> itemsByCategoryKey)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                GameItemRecord[]? records = JsonSerializer.Deserialize<GameItemRecord[]>(
                    File.ReadAllText(path),
                    options
                );

                if (records == null)
                {
                    return;
                }

                foreach (GameItemRecord record in records)
                {
                    if (record.ItemId <= 0 ||
                        string.IsNullOrWhiteSpace(record.Name))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(record.Category))
                    {
                        record.Category = "Unknown";
                    }

                    itemsByCategoryKey.TryAdd(BuildCategoryItemKey(record.Category, record.ItemId), record);

                    if (!items.ContainsKey(record.ItemId))
                    {
                        items.Add(record.ItemId, record);
                    }
                }
            }
            catch
            {
                // The generated database is a preferred cache, but row exports remain the fallback.
            }
        }

        private static void LoadCollectiveItemFile(
            string path,
            Dictionary<long, GameItemRecord> items,
            Dictionary<string, GameItemRecord> itemsByCategoryKey)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("items", out JsonElement itemArray) ||
                    itemArray.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                foreach (JsonElement item in itemArray.EnumerateArray())
                {
                    if (!TryGetInt64(item, "itemId", out long itemId) ||
                        itemId <= 0 ||
                        !TryGetString(item, "name", out string? name) ||
                        string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    string category = TryGetString(item, "category", out string? categoryValue) &&
                                      !string.IsNullOrWhiteSpace(categoryValue)
                        ? categoryValue
                        : "Unknown";

                    var record = new GameItemRecord
                    {
                        ItemId = itemId,
                        Category = category,
                        Name = name,
                        Description = TryGetString(item, "description", out string? description) ? description : null,
                        Effect = TryGetString(item, "effect", out string? effect) ? effect : null,
                        IconId = TryGetInt32(item, "iconId", out int iconId) ? iconId : null,
                        ImagePath = TryGetAssetPath(item, "image") ?? TryGetAssetPath(item, "icon")
                    };

                    itemsByCategoryKey[BuildCategoryItemKey(category, itemId)] = record;
                    items.TryAdd(itemId, record);
                }
            }
            catch
            {
                // Collective data is preferred when present, but the older item file remains the fallback.
            }
        }

        private static bool TryGetString(JsonElement element, string propertyName, out string? value)
        {
            value = null;
            if (!element.TryGetProperty(propertyName, out JsonElement property) ||
                property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = property.GetString();
            return true;
        }

        private static bool TryGetInt64(JsonElement element, string propertyName, out long value)
        {
            value = 0;
            return element.TryGetProperty(propertyName, out JsonElement property) &&
                   property.TryGetInt64(out value);
        }

        private static bool TryGetInt32(JsonElement element, string propertyName, out int value)
        {
            value = 0;
            return element.TryGetProperty(propertyName, out JsonElement property) &&
                   property.TryGetInt32(out value);
        }

        private static string? TryGetAssetPath(JsonElement item, string assetName)
        {
            if (!item.TryGetProperty("assets", out JsonElement assets) ||
                assets.ValueKind != JsonValueKind.Object ||
                !TryGetString(assets, assetName, out string? relativePath) ||
                string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            string path = Path.Combine(AppContext.BaseDirectory, relativePath);
            return File.Exists(path) ? path : null;
        }

        private static string BuildCategoryItemKey(string category, long itemId)
        {
            return $"{category.Trim()}|{itemId.ToString(CultureInfo.InvariantCulture)}";
        }

        private static void LoadEquipParamCsvIconMap(
            string path,
            string category,
            Dictionary<long, GameItemRecord> items)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                using StreamReader reader = new(path);
                string? headerLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(headerLine))
                {
                    return;
                }

                string[] headers = SplitCsvLine(headerLine).ToArray();
                int idIndex = Array.FindIndex(headers, h => string.Equals(h, "ID", StringComparison.OrdinalIgnoreCase));
                int nameIndex = Array.FindIndex(headers, h => string.Equals(h, "Name", StringComparison.OrdinalIgnoreCase));
                int iconIndex = Array.FindIndex(headers, h => string.Equals(h, "iconId", StringComparison.OrdinalIgnoreCase));

                if (idIndex < 0 || iconIndex < 0)
                {
                    return;
                }

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    string[] cells = SplitCsvLine(line).ToArray();
                    if (cells.Length <= Math.Max(idIndex, iconIndex) ||
                        !long.TryParse(cells[idIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out long itemId) ||
                        itemId <= 0 ||
                        !int.TryParse(cells[iconIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out int iconId) ||
                        iconId < 0)
                    {
                        continue;
                    }

                    string name = nameIndex >= 0 && cells.Length > nameIndex
                        ? cells[nameIndex].Trim()
                        : string.Empty;

                    if (!items.TryGetValue(itemId, out GameItemRecord? record))
                    {
                        record = new GameItemRecord
                        {
                            ItemId = itemId,
                            Category = category,
                            Name = string.IsNullOrWhiteSpace(name)
                                ? $"ITEM ID {itemId}"
                                : name
                        };
                        items[itemId] = record;
                    }

                    record.IconId = iconId;
                    if (!string.IsNullOrWhiteSpace(category))
                    {
                        record.Category = category;
                    }
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        record.Name = name;
                    }
                }
            }
            catch
            {
                // Param exports are optional; keep the generated database and row-name fallbacks usable.
            }
        }

        private static IEnumerable<string> SplitCsvLine(string line)
        {
            StringBuilder value = new();
            bool quoted = false;

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        value.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                    continue;
                }

                if (ch == ',' && !quoted)
                {
                    yield return value.ToString();
                    value.Clear();
                    continue;
                }

                value.Append(ch);
            }

            yield return value.ToString();
        }

        private static void LoadRowNameFile(
            string path,
            string category,
            Dictionary<long, GameItemRecord> items)
        {
            if (!File.Exists(path))
            {
                return;
            }

            foreach (string rawLine in File.ReadLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }

                string[] parts = line.Split(";;", 2, StringSplitOptions.None);
                if (parts.Length != 2 ||
                    !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long itemId))
                {
                    continue;
                }

                string name = parts[1].Trim();
                if (name.Length == 0 || items.ContainsKey(itemId))
                {
                    continue;
                }

                items.Add(itemId, new GameItemRecord
                {
                    ItemId = itemId,
                    Name = name,
                    Category = category
                });
            }
        }

        private static void LoadCheatTableNameMap(
            string path,
            Dictionary<long, GameItemRecord> items)
        {
            try
            {
                foreach (string rawLine in File.ReadLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length < 4)
                    {
                        continue;
                    }

                    int separator = line.IndexOf(':');
                    if (separator <= 0 || separator >= line.Length - 1)
                    {
                        continue;
                    }

                    string idText = line[..separator].Trim();
                    string name = line[(separator + 1)..].Trim();
                    if (name.Length == 0 ||
                        name.StartsWith("<", StringComparison.Ordinal) ||
                        name.Contains("?", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!TryParseTableId(idText, out long itemId) ||
                        itemId <= 0 ||
                        items.ContainsKey(itemId))
                    {
                        continue;
                    }

                    items.Add(itemId, new GameItemRecord
                    {
                        ItemId = itemId,
                        Name = name,
                        Category = "Table"
                    });
                }
            }
            catch
            {
                // Name maps are optional enrichment; the live reader should not depend on them.
            }
        }

        private static bool TryParseTableId(string idText, out long itemId)
        {
            NumberStyles style = idText.Any(char.IsLetter)
                ? NumberStyles.HexNumber
                : NumberStyles.Integer;

            return long.TryParse(
                idText,
                style,
                CultureInfo.InvariantCulture,
                out itemId
            );
        }
    }

    internal static class EnemyLoadoutTemplates
    {
        public static List<(string Slot, string Name)> Find(string enemyName, int paramId)
        {
            string normalized = Normalize(enemyName);

            if (normalized.Contains("godrick soldier") ||
                paramId == 43112010 ||
                paramId == 43114010)
            {
                return new List<(string Slot, string Name)>
                {
                    ("Primary Right", "Partisan"),
                    ("Primary Left", "Brass Shield"),
                    ("Helmet", "Godrick Soldier Helm"),
                    ("Armor", "Tree-and-Beast Surcoat"),
                    ("Gauntlets", "Godrick Soldier Gauntlets"),
                    ("Leggings", "Godrick Soldier Greaves")
                };
            }

            if (normalized.Contains("leyndell soldier"))
            {
                return new List<(string Slot, string Name)>
                {
                    ("Helmet", "Leyndell Soldier Helm"),
                    ("Armor", "Erdtree Surcoat"),
                    ("Gauntlets", "Leyndell Soldier Gauntlets"),
                    ("Leggings", "Leyndell Soldier Greaves")
                };
            }

            if (normalized.Contains("radahn soldier"))
            {
                return new List<(string Slot, string Name)>
                {
                    ("Helmet", "Radahn Soldier Helm"),
                    ("Armor", "Redmane Surcoat"),
                    ("Gauntlets", "Radahn Soldier Gauntlets"),
                    ("Leggings", "Radahn Soldier Greaves")
                };
            }

            return new List<(string Slot, string Name)>();
        }

        private static string Normalize(string value)
        {
            string cleaned = value
                .ToLowerInvariant()
                .Replace('-', ' ')
                .Replace('_', ' ');

            return string.Join(
                " ",
                cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            );
        }
    }

    internal sealed class EmptySlotControl : RoundedPanel
    {
        public EmptySlotControl(string slot)
        {
            BackColor = Color.FromArgb(13, 13, 16);
            BorderColor = SlotVisuals.BorderColor(slot, empty: true);
            BorderThickness = 1;
            CornerRadius = 9;
            Padding = new Padding(4);
            Cursor = Cursors.Default;

            var center = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(7, 7, 9)
            };

            Controls.Add(center);
        }
    }

    internal static class SlotVisuals
    {
        public static Color BorderColor(string slot, bool empty = false)
        {
            string normalized = slot.ToLowerInvariant();
            Color color;

            if (normalized.Contains("accessory"))
            {
                color = Color.FromArgb(128, 122, 208, 134);
            }
            else if (normalized.Contains("helmet") ||
                     normalized.Contains("armor") ||
                     normalized.Contains("gauntlet") ||
                     normalized.Contains("legging"))
            {
                color = Color.FromArgb(130, 116, 178, 192);
            }
            else if (normalized.Contains("arrow") ||
                     normalized.Contains("bolt"))
            {
                color = Color.FromArgb(120, 182, 164, 106);
            }
            else
            {
                color = Color.FromArgb(140, 92, 170, 204);
            }

            return empty
                ? Color.FromArgb(48, color)
                : color;
        }
    }

    internal sealed class ItemHistoryRowControl : RoundedPanel
    {
        private static readonly Color NormalBack = Color.FromArgb(28, 28, 32);
        private static readonly Color HoverBack = Color.FromArgb(38, 38, 44);
        private static readonly Color NormalBorder = Color.FromArgb(92, 255, 255, 255);
        private static readonly Color HoverBorder = Color.FromArgb(150, 255, 255, 255);
        private readonly ItemInfo _item;

        public event Action<Control, ItemInfo>? ItemHoverStarted;
        public event Action<Control>? ItemHoverEnded;
        public string EntryKey { get; }

        public ItemHistoryRowControl(ItemHistoryEntry entry, ItemInfo item, string entryKey)
        {
            _item = item;
            EntryKey = entryKey;
            BackColor = NormalBack;
            BorderColor = NormalBorder;
            BorderThickness = 1;
            CornerRadius = 12;
            Padding = new Padding(6);
            Cursor = Cursors.Hand;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 3,
                RowCount = 1
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var icon = new HighQualityImageBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(12, 12, 15),
                Image = MainFormImageLoader.Load(item.ImagePath)
            };

            var textLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(7, 0, 0, 0)
            };
            textLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            textLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));

            textLayout.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = item.Name,
                AutoEllipsis = true,
                ForeColor = Color.FromArgb(246, 246, 248),
                Font = new Font("Segoe UI Semibold", 8.7f),
                TextAlign = ContentAlignment.BottomLeft
            }, 0, 0);
            textLayout.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = string.IsNullOrWhiteSpace(entry.Category) ? "ITEM" : entry.Category.ToUpperInvariant(),
                AutoEllipsis = true,
                ForeColor = Color.FromArgb(158, 158, 166),
                Font = new Font("Consolas", 7.2f),
                TextAlign = ContentAlignment.TopLeft
            }, 0, 1);

            string timeText = entry.PickedUpAt.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            var meta = new Label
            {
                Dock = DockStyle.Fill,
                Text = $"x{Math.Max(1, entry.Quantity)}\n{timeText}",
                ForeColor = Color.FromArgb(210, 210, 216),
                Font = new Font("Consolas", 7.2f),
                TextAlign = ContentAlignment.MiddleRight
            };

            layout.Controls.Add(icon, 0, 0);
            layout.Controls.Add(textLayout, 1, 0);
            layout.Controls.Add(meta, 2, 0);
            Controls.Add(layout);

            WireHover(this);
        }

        private void WireHover(Control control)
        {
            control.MouseEnter += (_, _) =>
            {
                SetHot(true);
                ItemHoverStarted?.Invoke(this, _item);
            };
            control.MouseLeave += (_, _) =>
            {
                BeginInvoke(new Action(() =>
                {
                    if (!ClientRectangle.Contains(PointToClient(Control.MousePosition)))
                    {
                        SetHot(false);
                    }
                }));
                ItemHoverEnded?.Invoke(this);
            };

            foreach (Control child in control.Controls)
            {
                WireHover(child);
            }
        }

        private void SetHot(bool hot)
        {
            BackColor = hot ? HoverBack : NormalBack;
            BorderColor = hot ? HoverBorder : NormalBorder;
            Invalidate();
        }
    }

    internal sealed class ItemCardControl : RoundedPanel
    {
        private static readonly Color NormalBack = Color.FromArgb(19, 19, 23);
        private static readonly Color HoverBack = Color.FromArgb(30, 30, 35);
        private readonly HighQualityImageBox _icon;
        private readonly ItemInfo _item;
        private readonly Color _normalBorder;
        private readonly Color _hoverBorder;

        public event Action<Control, ItemInfo>? ItemHoverStarted;
        public event Action<Control>? ItemHoverEnded;

        public ItemCardControl(ItemInfo item)
        {
            _item = item;
            BackColor = NormalBack;
            _normalBorder = SlotVisuals.BorderColor(item.Slot);
            _hoverBorder = ControlPaint.Light(_normalBorder, 0.35f);
            BorderColor = _normalBorder;
            BorderThickness = 1;
            CornerRadius = 10;
            Padding = new Padding(5);
            Cursor = Cursors.Hand;

            _icon = new HighQualityImageBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(9, 9, 12),
                Image = MainFormImageLoader.Load(item.ImagePath)
            };

            Controls.Add(_icon);

            WireHover(this);
        }

        private void WireHover(Control control)
        {
            control.MouseEnter += (_, _) =>
            {
                SetHot(true);
                ItemHoverStarted?.Invoke(this, _item);
            };
            control.MouseLeave += (_, _) =>
            {
                BeginInvoke(new Action(() =>
                {
                    if (!ClientRectangle.Contains(PointToClient(Control.MousePosition)))
                    {
                        SetHot(false);
                    }
                }));
                ItemHoverEnded?.Invoke(this);
            };

            foreach (Control child in control.Controls)
            {
                WireHover(child);
            }
        }

        private void SetHot(bool hot)
        {
            BackColor = hot ? HoverBack : NormalBack;
            BorderColor = hot ? _hoverBorder : _normalBorder;
            BorderThickness = hot ? 2 : 1;
            Padding = hot ? new Padding(4) : new Padding(5);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Image? image = _icon.Image;
                _icon.Image = null;
                image?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class HighQualityImageBox : Control
    {
        private Image? _image;

        public HighQualityImageBox()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        public Image? Image
        {
            get => _image;
            set
            {
                _image = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using SolidBrush background = new SolidBrush(BackColor);
            e.Graphics.FillRectangle(background, ClientRectangle);

            if (_image == null || Width <= 0 || Height <= 0)
            {
                return;
            }

            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.SmoothingMode = SmoothingMode.HighQuality;

            Rectangle target = GetZoomRectangle(_image.Size, ClientRectangle);
            e.Graphics.DrawImage(_image, target);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _image?.Dispose();
                _image = null;
            }

            base.Dispose(disposing);
        }

        private static Rectangle GetZoomRectangle(Size imageSize, Rectangle bounds)
        {
            if (imageSize.Width <= 0 || imageSize.Height <= 0 ||
                bounds.Width <= 0 || bounds.Height <= 0)
            {
                return Rectangle.Empty;
            }

            float scale = Math.Min(
                (float)bounds.Width / imageSize.Width,
                (float)bounds.Height / imageSize.Height
            );

            int width = Math.Max(1, (int)Math.Round(imageSize.Width * scale));
            int height = Math.Max(1, (int)Math.Round(imageSize.Height * scale));
            return new Rectangle(
                bounds.X + (bounds.Width - width) / 2,
                bounds.Y + (bounds.Height - height) / 2,
                width,
                height
            );
        }
    }

    internal static class MainFormImageLoader
    {
        private const int MaxCachedImages = 48;
        private const int MaxCachedImageDimension = 512;
        private static readonly object CacheLock = new();
        private static readonly Dictionary<string, CachedImage> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> CacheOrder = new();

        public static Image? Load(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                string fullPath = Path.GetFullPath(path);
                FileInfo file = new(fullPath);
                lock (CacheLock)
                {
                    if (Cache.TryGetValue(fullPath, out CachedImage? cached) &&
                        cached.Length == file.Length &&
                        cached.LastWriteTimeUtc == file.LastWriteTimeUtc)
                    {
                        return new Bitmap(cached.Image);
                    }

                    if (cached != null)
                    {
                        Cache.Remove(fullPath);
                        cached.Image.Dispose();
                    }
                }

                using FileStream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using Image source = Image.FromStream(stream);
                Bitmap image = CreateDisplayBitmap(source);

                lock (CacheLock)
                {
                    Cache[fullPath] = new CachedImage(image, file.Length, file.LastWriteTimeUtc);
                    CacheOrder.Enqueue(fullPath);
                    TrimCache();
                    return new Bitmap(image);
                }
            }
            catch
            {
                return null;
            }
        }

        public static void Clear()
        {
            lock (CacheLock)
            {
                foreach (CachedImage cached in Cache.Values)
                {
                    cached.Image.Dispose();
                }

                Cache.Clear();
                CacheOrder.Clear();
            }
        }

        private static void TrimCache()
        {
            while (Cache.Count > MaxCachedImages && CacheOrder.Count > 0)
            {
                string key = CacheOrder.Dequeue();
                if (!Cache.Remove(key, out CachedImage? cached))
                {
                    continue;
                }

                cached.Image.Dispose();
            }
        }

        private static Bitmap CreateDisplayBitmap(Image source)
        {
            if (source.Width <= MaxCachedImageDimension &&
                source.Height <= MaxCachedImageDimension)
            {
                return new Bitmap(source);
            }

            float scale = Math.Min(
                (float)MaxCachedImageDimension / source.Width,
                (float)MaxCachedImageDimension / source.Height);
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
            var bitmap = new Bitmap(width, height);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            return bitmap;
        }

        private sealed record CachedImage(Bitmap Image, long Length, DateTime LastWriteTimeUtc);
    }

    internal sealed class ItemHoverOverlayForm : UserControl
    {
        private readonly HighQualityImageBox _image;
        private readonly Label _name;
        private readonly Label _meta;
        private readonly TextBox _description;

        public ItemHoverOverlayForm()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.SupportsTransparentBackColor,
                true);

            Width = 520;
            Height = 330;
            Visible = false;
            BackColor = Color.Transparent;
            ForeColor = Color.FromArgb(244, 244, 246);
            Padding = new Padding(16, 12, 18, 18);

            var border = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(26, 26, 30),
                BorderColor = Color.FromArgb(145, 255, 255, 255),
                BorderThickness = 1,
                CornerRadius = 22,
                Padding = new Padding(16)
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 2,
                RowCount = 3
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 166));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _image = new HighQualityImageBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(13, 13, 16),
                Margin = new Padding(0, 0, 14, 0)
            };

            _name = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(245, 245, 247),
                Font = new Font("Segoe UI Semibold", 13.4f),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.BottomLeft
            };

            _meta = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(145, 145, 153),
                Font = new Font("Consolas", 8.5f),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.TopLeft
            };

            _description = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(26, 26, 30),
                ForeColor = Color.FromArgb(196, 196, 202),
                BorderStyle = BorderStyle.None,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Segoe UI", 9.7f),
                TabStop = false
            };

            layout.Controls.Add(_image, 0, 0);
            layout.SetRowSpan(_image, 3);
            layout.Controls.Add(_name, 1, 0);
            layout.Controls.Add(_meta, 1, 1);
            layout.Controls.Add(_description, 1, 2);
            border.Controls.Add(layout);
            Controls.Add(border);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle card = new(
                Padding.Left,
                Padding.Top,
                Math.Max(1, Width - Padding.Horizontal),
                Math.Max(1, Height - Padding.Vertical));

            DrawShadow(e.Graphics, card, 22);
        }

        private static void DrawShadow(Graphics graphics, Rectangle card, int radius)
        {
            for (int layer = 7; layer >= 1; layer--)
            {
                int alpha = 12 + (8 - layer) * 6;
                Rectangle shadowRect = card;
                shadowRect.Offset(0, layer + 2);
                shadowRect.Inflate(layer * 2, layer * 2);
                using GraphicsPath shadowPath = RoundedPanel.CreateRoundedPath(shadowRect, radius + layer);
                using SolidBrush shadow = new(Color.FromArgb(alpha, 0, 0, 0));
                graphics.FillPath(shadow, shadowPath);
            }

            Rectangle lip = card;
            lip.Offset(0, 2);
            using GraphicsPath lipPath = RoundedPanel.CreateRoundedPath(lip, radius);
            using SolidBrush lipBrush = new(Color.FromArgb(56, 0, 0, 0));
            graphics.FillPath(lipBrush, lipPath);
        }

        public void SetItem(ItemInfo item)
        {
            Image? oldImage = _image.Image;
            _image.Image = MainFormImageLoader.Load(item.ImagePath);
            oldImage?.Dispose();

            _name.Text = item.Name.ToUpperInvariant();
            string quantity = item.Quantity > 1
                ? $"   /   x{item.Quantity}"
                : string.Empty;
            _meta.Text = $"{item.Slot.ToUpperInvariant()}   /   {item.Category.ToUpperInvariant()}   /   ID {item.ItemId}{quantity}";
            _description.Text = string.IsNullOrWhiteSpace(item.DisplayDescription)
                ? "Description unavailable."
                : item.DisplayDescription;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Image? image = _image.Image;
                _image.Image = null;
                image?.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    internal sealed class CharacterInfo
    {
        public ulong Address { get; set; }
        public uint LocalId { get; set; }
        public ushort GlobalId { get; set; }
        public int ParamId { get; set; }
        public int CharaInitParamId { get; set; }
        public string CharaInitParamSource { get; set; } = "not resolved";
        public int CharaInitCandidateParamId { get; set; }
        public string CharaInitCandidateSource { get; set; } = "not resolved";
        public int ClearCount { get; set; } = -1;
        public int CurrentHp { get; set; }
        public int MaxHp { get; set; }
        public StaggerInfo? Stagger { get; set; }
        public IReadOnlyList<EquipmentItemProbe> Equipment { get; set; } =
            Array.Empty<EquipmentItemProbe>();
    }

    internal sealed record StaggerInfo(float Current, float Maximum);

    internal sealed record CharaInitProbeResult(
        int ParamId,
        string Source,
        int CandidateParamId,
        string CandidateSource,
        int Score = 0)
    {
        public static readonly CharaInitProbeResult None = new(0, "not resolved", 0, "not resolved");
    }

    internal sealed record EquipmentScoutItem(
        ulong Offset,
        int ItemId,
        string Name,
        string Category);

    internal sealed record EquipmentScoutCluster(
        string Source,
        ulong BaseAddress,
        ulong WindowOffset,
        IReadOnlyList<EquipmentScoutItem> Items,
        int Score);

    internal sealed record EquipmentItemProbe(string Slot, int ItemId)
    {
        public static readonly IReadOnlyList<(string Slot, ulong Offset)> Slots =
            new (string Slot, ulong Offset)[]
            {
                ("Primary Left", 0x398),
                ("Primary Right", 0x39C),
                ("Secondary Left", 0x3A0),
                ("Secondary Right", 0x3A4),
                ("Tertiary Left", 0x3A8),
                ("Tertiary Right", 0x3AC),
                ("Primary Arrow", 0x3B0),
                ("Primary Bolt", 0x3B4),
                ("Secondary Arrow", 0x3B8),
                ("Secondary Bolt", 0x3BC),
                ("Tertiary Arrow", 0x3C0),
                ("Tertiary Bolt", 0x3C4),
                ("Helmet", 0x3C8),
                ("Armor", 0x3CC),
                ("Gauntlet", 0x3D0),
                ("Leggings", 0x3D4),
                ("Accessory 1", 0x3DC),
                ("Accessory 2", 0x3E0),
                ("Accessory 3", 0x3E4),
                ("Accessory 4", 0x3E8),
                ("Accessory 5", 0x3EC)
            };
    }

    internal sealed class LockOnHook : IDisposable
    {
        private const int AllocationSize = 0x1000;
        private const int TargetStorageOffset = 0x00;
        private const int ManagerStorageOffset = 0x08;
        private const int HookCodeOffset = 0x20;
        private const int OverwriteLength = 11;

        private readonly MemoryReader _memory;
        private readonly ulong _hookAddress;
        private readonly byte[] _originalBytes;
        private bool _installed;

        private LockOnHook(
            MemoryReader memory,
            ulong hookAddress,
            ulong allocationAddress,
            byte[] originalBytes)
        {
            _memory = memory;
            _hookAddress = hookAddress;
            AllocationAddress = allocationAddress;
            _originalBytes = originalBytes;
            _installed = true;
        }

        public ulong AllocationAddress { get; }

        public ulong TargetStorageAddress =>
            AllocationAddress + TargetStorageOffset;

        public ulong ManagerStorageAddress =>
            AllocationAddress + ManagerStorageOffset;

        public static LockOnHook Install(
            MemoryReader memory,
            ulong hookAddress)
        {
            byte[] originalBytes =
                memory.ReadBytes(
                    hookAddress,
                    OverwriteLength
                );

            byte[] expectedBytes =
            {
                0x48, 0x8B, 0x48, 0x08,
                0x49, 0x89, 0x8D, 0xB0,
                0x06, 0x00, 0x00
            };

            if (!BytesEqual(
                    originalBytes,
                    expectedBytes))
            {
                if (!LooksLikeInstalledPatch(originalBytes) ||
                    IsAnotherReaderInstanceRunning())
                {
                    throw new InvalidOperationException(
                        "Lock-on bytes are already modified. " +
                        "Close other reader instances and restart Elden Ring."
                    );
                }

                memory.WriteExecutableBytes(
                    hookAddress,
                    expectedBytes
                );

                Thread.Sleep(25);
                originalBytes = expectedBytes.ToArray();
            }

            ulong allocationAddress =
                memory.AllocateNear(
                    hookAddress,
                    AllocationSize
                );

            try
            {
                ulong codeAddress =
                    allocationAddress +
                    HookCodeOffset;

                List<byte> code = new();

                AppendRipRelativeStore(
                    code,
                    codeAddress,
                    0x48,
                    0x89,
                    0x05,
                    allocationAddress +
                    TargetStorageOffset
                );

                AppendRipRelativeStore(
                    code,
                    codeAddress,
                    0x4C,
                    0x89,
                    0x2D,
                    allocationAddress +
                    ManagerStorageOffset
                );

                code.AddRange(originalBytes);

                ulong jumpInstructionAddress =
                    codeAddress +
                    (ulong)code.Count;

                code.Add(0xE9);

                int jumpBackDisplacement =
                    CalculateRelativeDisplacement(
                        jumpInstructionAddress,
                        5,
                        hookAddress +
                        OverwriteLength
                    );

                code.AddRange(
                    BitConverter.GetBytes(
                        jumpBackDisplacement
                    )
                );

                memory.WriteBytes(
                    allocationAddress,
                    new byte[AllocationSize]
                );

                memory.WriteBytes(
                    codeAddress,
                    code.ToArray()
                );

                byte[] patch =
                    new byte[OverwriteLength];

                patch[0] = 0xE9;

                int hookDisplacement =
                    CalculateRelativeDisplacement(
                        hookAddress,
                        5,
                        codeAddress
                    );

                Array.Copy(
                    BitConverter.GetBytes(hookDisplacement),
                    0,
                    patch,
                    1,
                    4
                );

                for (int index = 5;
                     index < patch.Length;
                     index++)
                {
                    patch[index] = 0x90;
                }

                memory.WriteExecutableBytes(
                    hookAddress,
                    patch
                );

                return new LockOnHook(
                    memory,
                    hookAddress,
                    allocationAddress,
                    originalBytes
                );
            }
            catch
            {
                memory.Free(allocationAddress);
                throw;
            }
        }

        private static void AppendRipRelativeStore(
            List<byte> code,
            ulong codeBaseAddress,
            byte rex,
            byte opcode,
            byte modRm,
            ulong destinationAddress)
        {
            ulong instructionAddress =
                codeBaseAddress +
                (ulong)code.Count;

            code.Add(rex);
            code.Add(opcode);
            code.Add(modRm);

            int displacement =
                CalculateRelativeDisplacement(
                    instructionAddress,
                    7,
                    destinationAddress
                );

            code.AddRange(
                BitConverter.GetBytes(displacement)
            );
        }

        private static int CalculateRelativeDisplacement(
            ulong instructionAddress,
            int instructionLength,
            ulong destinationAddress)
        {
            long displacement =
                checked(
                    (long)destinationAddress -
                    ((long)instructionAddress +
                     instructionLength)
                );

            if (displacement < int.MinValue ||
                displacement > int.MaxValue)
            {
                throw new InvalidOperationException(
                    "Hook allocation is too far from game code."
                );
            }

            return (int)displacement;
        }

        private static bool BytesEqual(
            byte[] left,
            byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int index = 0;
                 index < left.Length;
                 index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool LooksLikeInstalledPatch(byte[] bytes)
        {
            if (bytes.Length != OverwriteLength ||
                bytes[0] != 0xE9)
            {
                return false;
            }

            for (int index = 5;
                 index < bytes.Length;
                 index++)
            {
                if (bytes[index] != 0x90)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAnotherReaderInstanceRunning()
        {
            using Process current =
                Process.GetCurrentProcess();

            return Process
                .GetProcessesByName(
                    current.ProcessName
                )
                .Any(process =>
                {
                    try
                    {
                        return process.Id != current.Id;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                });
        }

        public void Dispose()
        {
            if (!_installed)
            {
                return;
            }

            _installed = false;

            try
            {
                if (!_memory.Process.HasExited)
                {
                    _memory.WriteExecutableBytes(
                        _hookAddress,
                        _originalBytes
                    );

                    Thread.Sleep(25);
                    _memory.Free(AllocationAddress);
                }
            }
            catch
            {
                // Process shutdown can make cleanup impossible.
            }
        }
    }

    internal sealed class MemoryReadException : Exception
    {
        public MemoryReadException(string message)
            : base(message)
        {
        }
    }

    internal sealed class MemoryReader : IDisposable
    {
        private const uint ProcessVmOperation = 0x0008;
        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessVmWrite = 0x0020;
        private const uint ProcessQueryInformation = 0x0400;
        private const uint MemCommit = 0x1000;
        private const uint MemReserve = 0x2000;
        private const uint MemRelease = 0x8000;
        private const uint PageExecuteReadWrite = 0x40;
        private const ulong AllocationGranularity = 0x10000;
        private const ulong MaximumRelativeDistance = 0x70000000;

        private readonly IntPtr _processHandle;

        public MemoryReader(string processName)
        {
            Process[] processes =
                Process.GetProcessesByName(
                    processName
                );

            if (processes.Length == 0)
            {
                throw new InvalidOperationException(
                    processName +
                    ".exe is not running."
                );
            }

            Process = processes[0];

            _processHandle =
                OpenProcess(
                    ProcessVmOperation |
                    ProcessVmRead |
                    ProcessVmWrite |
                    ProcessQueryInformation,
                    false,
                    Process.Id
                );

            if (_processHandle == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not open Elden Ring. Run as administrator."
                );
            }

            ProcessModule? mainModule =
                Process.MainModule;

            if (mainModule == null)
            {
                throw new InvalidOperationException(
                    "Could not access eldenring.exe."
                );
            }

            ModuleBase =
                unchecked(
                    (ulong)mainModule
                        .BaseAddress
                        .ToInt64()
                );

            ModuleSize =
                mainModule.ModuleMemorySize;
        }

        public Process Process { get; }
        public ulong ModuleBase { get; }
        public int ModuleSize { get; }

        public bool IsLikelyPointer(ulong address)
        {
            return address >= 0x10000 &&
                   address <= 0x00007FFFFFFFFFFF;
        }

        public ushort ReadUInt16(ulong address) =>
            BitConverter.ToUInt16(
                ReadBytes(address, sizeof(ushort)),
                0
            );

        public int ReadInt32(ulong address) =>
            BitConverter.ToInt32(
                ReadBytes(address, sizeof(int)),
                0
            );

        public uint ReadUInt32(ulong address) =>
            BitConverter.ToUInt32(
                ReadBytes(address, sizeof(uint)),
                0
            );

        public float ReadSingle(ulong address) =>
            BitConverter.ToSingle(
                ReadBytes(address, sizeof(float)),
                0
            );

        public ulong ReadUInt64(ulong address) =>
            BitConverter.ToUInt64(
                ReadBytes(address, sizeof(ulong)),
                0
            );

        public byte[] ReadBytes(
            ulong address,
            int count)
        {
            if (address == 0)
            {
                throw new MemoryReadException(
                    "Attempted to read address 0."
                );
            }

            byte[] buffer = new byte[count];

            bool success =
                ReadProcessMemory(
                    _processHandle,
                    new IntPtr(
                        unchecked((long)address)
                    ),
                    buffer,
                    count,
                    out IntPtr bytesRead
                );

            if (!success ||
                bytesRead.ToInt64() != count)
            {
                int error =
                    Marshal.GetLastWin32Error();

                throw new MemoryReadException(
                    $"Read failed at 0x{address:X}. " +
                    $"Win32 error: {error}."
                );
            }

            return buffer;
        }

        public void WriteBytes(
            ulong address,
            byte[] bytes)
        {
            bool success =
                WriteProcessMemory(
                    _processHandle,
                    new IntPtr(
                        unchecked((long)address)
                    ),
                    bytes,
                    bytes.Length,
                    out IntPtr bytesWritten
                );

            if (!success ||
                bytesWritten.ToInt64() != bytes.Length)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Write failed at 0x{address:X}."
                );
            }
        }

        public void WriteExecutableBytes(
            ulong address,
            byte[] bytes)
        {
            if (!VirtualProtectEx(
                    _processHandle,
                    new IntPtr(
                        unchecked((long)address)
                    ),
                    (UIntPtr)bytes.Length,
                    PageExecuteReadWrite,
                    out uint oldProtection))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not change protection at 0x{address:X}."
                );
            }

            try
            {
                WriteBytes(address, bytes);

                FlushInstructionCache(
                    _processHandle,
                    new IntPtr(
                        unchecked((long)address)
                    ),
                    (UIntPtr)bytes.Length
                );
            }
            finally
            {
                VirtualProtectEx(
                    _processHandle,
                    new IntPtr(
                        unchecked((long)address)
                    ),
                    (UIntPtr)bytes.Length,
                    oldProtection,
                    out _
                );
            }
        }

        public ulong AllocateNear(
            ulong targetAddress,
            int size)
        {
            ulong alignedTarget =
                targetAddress &
                ~(AllocationGranularity - 1);

            for (ulong distance = 0;
                 distance <= MaximumRelativeDistance;
                 distance += AllocationGranularity)
            {
                if (TryAllocateAt(
                        alignedTarget + distance,
                        size,
                        out ulong upperAddress))
                {
                    return upperAddress;
                }

                if (distance == 0 ||
                    alignedTarget < distance)
                {
                    continue;
                }

                if (TryAllocateAt(
                        alignedTarget - distance,
                        size,
                        out ulong lowerAddress))
                {
                    return lowerAddress;
                }
            }

            throw new InvalidOperationException(
                "Could not allocate hook memory near game code."
            );
        }

        private bool TryAllocateAt(
            ulong preferredAddress,
            int size,
            out ulong allocatedAddress)
        {
            allocatedAddress = 0;

            if (!IsLikelyPointer(preferredAddress))
            {
                return false;
            }

            IntPtr result =
                VirtualAllocEx(
                    _processHandle,
                    new IntPtr(
                        unchecked((long)preferredAddress)
                    ),
                    (UIntPtr)size,
                    MemCommit | MemReserve,
                    PageExecuteReadWrite
                );

            if (result == IntPtr.Zero)
            {
                return false;
            }

            allocatedAddress =
                unchecked(
                    (ulong)result.ToInt64()
                );

            long difference =
                (long)allocatedAddress -
                (long)preferredAddress;

            if (Math.Abs(difference) >
                (long)AllocationGranularity)
            {
                Free(allocatedAddress);
                allocatedAddress = 0;
                return false;
            }

            return true;
        }

        public void Free(ulong address)
        {
            if (address == 0)
            {
                return;
            }

            VirtualFreeEx(
                _processHandle,
                new IntPtr(
                    unchecked((long)address)
                ),
                UIntPtr.Zero,
                MemRelease
            );
        }


        public ulong ScanModule(string patternText)
        {
            PatternByte[] pattern =
                ParsePattern(patternText);

            const int chunkSize = 64 * 1024;
            int overlap = Math.Max(0, pattern.Length - 1);

            for (int offset = 0;
                 offset < ModuleSize;
                 offset += chunkSize)
            {
                int remaining = ModuleSize - offset;

                int requested =
                    Math.Min(
                        chunkSize + overlap,
                        remaining
                    );

                ulong address =
                    ModuleBase +
                    (ulong)offset;

                byte[] bytes;

                try
                {
                    bytes = ReadBytes(address, requested);
                }
                catch
                {
                    continue;
                }

                int foundIndex =
                    FindPattern(bytes, pattern);

                if (foundIndex >= 0)
                {
                    return address +
                           (ulong)foundIndex;
                }
            }

            throw new InvalidOperationException(
                "Lock-on signature was not found. " +
                "Close other reader instances and restart Elden Ring."
            );
        }

        private static PatternByte[] ParsePattern(
            string patternText)
        {
            string[] tokens =
                patternText.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries
                );

            PatternByte[] pattern =
                new PatternByte[tokens.Length];

            for (int index = 0;
                 index < tokens.Length;
                 index++)
            {
                string token = tokens[index];

                if (token == "?" ||
                    token == "??")
                {
                    pattern[index] =
                        new PatternByte(0, true);
                }
                else
                {
                    pattern[index] =
                        new PatternByte(
                            byte.Parse(
                                token,
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture
                            ),
                            false
                        );
                }
            }

            return pattern;
        }

        private static int FindPattern(
            byte[] data,
            PatternByte[] pattern)
        {
            if (data.Length < pattern.Length)
            {
                return -1;
            }

            int finalStart =
                data.Length -
                pattern.Length;

            for (int start = 0;
                 start <= finalStart;
                 start++)
            {
                bool match = true;

                for (int index = 0;
                     index < pattern.Length;
                     index++)
                {
                    if (pattern[index].Wildcard)
                    {
                        continue;
                    }

                    if (data[start + index] !=
                        pattern[index].Value)
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return start;
                }
            }

            return -1;
        }

        public void Dispose()
        {
            if (_processHandle != IntPtr.Zero)
            {
                CloseHandle(_processHandle);
            }

            Process.Dispose();
        }

        private readonly struct PatternByte
        {
            public PatternByte(
                byte value,
                bool wildcard)
            {
                Value = value;
                Wildcard = wildcard;
            }

            public byte Value { get; }
            public bool Wildcard { get; }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint desiredAccess,
            bool inheritHandle,
            int processId
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(
            IntPtr processHandle,
            IntPtr baseAddress,
            [Out] byte[] buffer,
            int size,
            out IntPtr numberOfBytesRead
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteProcessMemory(
            IntPtr processHandle,
            IntPtr baseAddress,
            byte[] buffer,
            int size,
            out IntPtr numberOfBytesWritten
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(
            IntPtr processHandle,
            IntPtr address,
            UIntPtr size,
            uint allocationType,
            uint protection
        );



        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualFreeEx(
            IntPtr processHandle,
            IntPtr address,
            UIntPtr size,
            uint freeType
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualProtectEx(
            IntPtr processHandle,
            IntPtr address,
            UIntPtr size,
            uint newProtection,
            out uint oldProtection
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlushInstructionCache(
            IntPtr processHandle,
            IntPtr baseAddress,
            UIntPtr size
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(
            IntPtr handle
        );
    }
}
