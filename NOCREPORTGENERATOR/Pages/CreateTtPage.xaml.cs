using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Message = MsgReader.Outlook.Storage.Message;
using NOCREPORTGENERATOR.Models;
using NOCREPORTGENERATOR.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace NOCREPORTGENERATOR.Pages
{
    public sealed partial class CreateTtPage : Page
    {
        private const string DateTimeInputFormat = "dd-MM-yyyy HH:mm";
        private const int MaxSavedFormsInCombo = 1000;
        private static readonly Regex TtIohRegex = new(@"INC-\d{8}-\d{8}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly HashSet<string> ImageFileExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".heic" };
        private CancellationTokenSource? _systemKeyLookupCts;
        private CancellationTokenSource? _coordinateImageParseCts;
        private Dictionary<string, IReadOnlyList<string>> _picBySegmentRoute = new(StringComparer.OrdinalIgnoreCase);
        private List<string> _allSegmentRoutes = new();
        private List<string> _allPicOptions = new();
        private List<LocalFormRecord> _savedForms = new();
        private readonly Dictionary<string, FormTabState> _tabStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _tabOrder = new();
        private string? _activeTabId;
        private int _draftTabCounter;
        private bool _allPicOptionsLoaded;
        private bool _isPopulatingSegmentOptions;
        private bool _isApplyingSavedForm;
        private bool _isSwitchingTabs;
        private bool _isHandlingTabSelectionChanged;
        private bool _pendingTabRefresh;
        private bool _isUpdatingTtIohText;
        private bool _statusLinkOptionsLoaded;
        private List<string> _statusLinkOptions = new();
        private readonly List<ImpactListItem> _impactListItems = new();
        private static readonly IReadOnlyList<string> ImpactStatusOptions = new[] { "Down ❌", "Up ✅", "Cancel ⛔" };
        private string _pendingStatusLink = string.Empty;

        public CreateTtPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;

            var now = DateTimeOffset.Now;
            OccurTimeTextBox.Text = FormatDateTime(now);
            DispatchTimeTextBox.Text = FormatDateTime(now);

            InitializeTabs();
            RefreshImpactListUi();
            SetCoordinatePhotoStatus(string.IsNullOrWhiteSpace(AppSettingsService.GetGeminiApiKey())
                ? "API key Gemini belum diisi. Isi di halaman Settings."
                : "Siap proses foto coordinate dengan Gemini AI.");
            UpdateTemplatePreview();
            _ = TryAutoFillSegmentRouteFromSystemKeyAsync();
            _ = EnsureAllPicOptionsLoadedAsync();
            _ = EnsureStatusLinkOptionsLoadedAsync();
            _ = RefreshSavedFormsAsync();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var pendingDraft = PendingDraftTransferService.Consume();
            if (pendingDraft is null)
            {
                return;
            }

            try
            {
                SaveActiveTabState();
                ApplySavedForm(pendingDraft);
                SaveActiveTabState();
                SetMsgStatus("Draft loaded dari Dashboard: " + pendingDraft.Name);
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("CreateTtPage.OnNavigatedTo", ex);
            }
        }

        private void InitializeTabs()
        {
            var initialState = CaptureFormState();
            var initialTabId = CreateNewTab(initialState);
            SelectTab(initialTabId, saveCurrentState: false);
        }

        private string CreateNewTab(FormTabState state)
        {
            _draftTabCounter++;
            state.TabId = string.IsNullOrWhiteSpace(state.TabId) ? Guid.NewGuid().ToString("N") : state.TabId;
            if (string.IsNullOrWhiteSpace(state.Header))
            {
                state.Header = "Draft " + _draftTabCounter;
            }

            _tabStates[state.TabId] = state;
            if (!_tabOrder.Contains(state.TabId, StringComparer.OrdinalIgnoreCase))
            {
                _tabOrder.Add(state.TabId);
            }

            RefreshTabOptions();
            return state.TabId;
        }

        private void RefreshTabOptions()
        {
            if (_isHandlingTabSelectionChanged)
            {
                QueueRefreshTabOptions();
                return;
            }

            RefreshTabOptionsCore();
        }

        private void QueueRefreshTabOptions()
        {
            if (_pendingTabRefresh)
            {
                return;
            }

            _pendingTabRefresh = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _pendingTabRefresh = false;
                if (_isHandlingTabSelectionChanged)
                {
                    QueueRefreshTabOptions();
                    return;
                }

                RefreshTabOptionsCore();
            });
        }

        private void RefreshTabOptionsCore()
        {
            var selectedId = _activeTabId;
            _isSwitchingTabs = true;

            try
            {
                FormTabsTabView.TabItems.Clear();
                TabViewItem? selectedItem = null;

                foreach (var id in _tabOrder.Where(id => _tabStates.ContainsKey(id)))
                {
                    var item = new TabViewItem
                    {
                        Header = GetTabHeaderLabel(_tabStates[id]),
                        Tag = id,
                        IsClosable = _tabOrder.Count > 1
                    };
                    item.DoubleTapped += TabViewItem_DoubleTapped;
                    item.ContextFlyout = CreateTabContextFlyout(id);

                    FormTabsTabView.TabItems.Add(item);
                    if (!string.IsNullOrWhiteSpace(selectedId) &&
                        string.Equals(id, selectedId, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedItem = item;
                    }
                }

                if (selectedItem is null && FormTabsTabView.TabItems.Count > 0)
                {
                    selectedItem = FormTabsTabView.TabItems[0] as TabViewItem;
                }

                FormTabsTabView.SelectedItem = selectedItem;
            }
            finally
            {
                _isSwitchingTabs = false;
            }
        }

        private void SelectTab(string tabId, bool saveCurrentState = true)
        {
            if (string.IsNullOrWhiteSpace(tabId) || !_tabStates.ContainsKey(tabId))
            {
                return;
            }

            if (saveCurrentState)
            {
                SaveActiveTabState();
            }

            _activeTabId = tabId;
            RefreshTabOptions();
            LoadTabState(tabId);
        }

        private void SaveActiveTabState()
        {
            if (string.IsNullOrWhiteSpace(_activeTabId))
            {
                return;
            }

            var state = CaptureFormState();
            state.TabId = _activeTabId;
            if (_tabStates.TryGetValue(_activeTabId, out var current))
            {
                state.Header = current.Header;
                state.IsDirty = current.IsDirty;
                state.IsHeaderManuallyEdited = current.IsHeaderManuallyEdited;
                state.SavedRecordId = current.SavedRecordId;
                state.SavedRecordName = current.SavedRecordName;
            }

            _tabStates[_activeTabId] = state;
            UpdateActiveTabHeaderFromState(state);
        }

        private void LoadTabState(string tabId)
        {
            if (!_tabStates.TryGetValue(tabId, out var state))
            {
                return;
            }

            ApplyFormState(state);
            _activeTabId = tabId;
            UpdateActiveTabHeaderFromState(state);
        }

        private void CreateAndSelectNewTab()
        {
            SaveActiveTabState();
            var now = DateTimeOffset.Now;
            var state = new FormTabState
            {
                Header = "Draft " + (_draftTabCounter + 1),
                OccurDateTime = now,
                DispatchDateTime = now,
                ShowSegmentRoute = true,
                ShowSystemKey = true
            };

            var tabId = CreateNewTab(state);
            SelectTab(tabId);
            _ = TryAutoFillSegmentRouteFromSystemKeyAsync();
        }

        private void DuplicateActiveTab()
        {
            SaveActiveTabState();
            if (string.IsNullOrWhiteSpace(_activeTabId) || !_tabStates.TryGetValue(_activeTabId, out var source))
            {
                return;
            }

            DuplicateTab(source.TabId);
        }

        private static FormTabState CloneTabState(FormTabState source)
        {
            return new FormTabState
            {
                Header = source.Header,
                IsHeaderManuallyEdited = source.IsHeaderManuallyEdited,
                IsDirty = source.IsDirty,
                TtIoh = source.TtIoh,
                Title = source.Title,
                OccurDateTime = source.OccurDateTime,
                DispatchDateTime = source.DispatchDateTime,
                StatusLink = source.StatusLink,
                Pic = source.Pic,
                RootCause = source.RootCause,
                CutPoint = source.CutPoint,
                ShowSegmentRoute = source.ShowSegmentRoute,
                ShowSystemKey = source.ShowSystemKey,
                SegmentRoute = source.SegmentRoute,
                SystemKey = source.SystemKey,
                Coordinate = source.Coordinate,
                UpdateProgress = source.UpdateProgress,
                DraftName = source.DraftName,
                ImpactList = CloneImpactList(source.ImpactList)
            };
        }

        private void DuplicateTabButton_Click(object sender, RoutedEventArgs e)
        {
            DuplicateActiveTab();
        }

        private void NewTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            CreateAndSelectNewTab();
            args.Handled = true;
        }

        private async void CloseTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            if (string.IsNullOrWhiteSpace(_activeTabId))
            {
                return;
            }

            SaveActiveTabState();
            await CloseTabAsync(_activeTabId);
        }

        private async void TabViewItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is not TabViewItem item || item.Tag is not string tabId)
            {
                return;
            }

            await RenameTabAsync(tabId);
        }

        private void DuplicateTab(string sourceTabId)
        {
            SaveActiveTabState();
            if (string.IsNullOrWhiteSpace(sourceTabId) || !_tabStates.TryGetValue(sourceTabId, out var source))
            {
                return;
            }

            var duplicate = CloneTabState(source);
            duplicate.TabId = string.Empty;
            duplicate.Header = source.Header + " Copy";
            duplicate.IsDirty = true;
            duplicate.IsHeaderManuallyEdited = true;

            var tabId = CreateNewTab(duplicate);
            SelectTab(tabId);
            SetMsgStatus("Tab diduplikasi.");
        }

        private MenuFlyout CreateTabContextFlyout(string tabId)
        {
            var flyout = new MenuFlyout();

            var renameItem = new MenuFlyoutItem
            {
                Text = "Rename",
                Icon = new SymbolIcon(Symbol.Edit),
                Tag = tabId
            };
            renameItem.Click += RenameTabMenuItem_Click;
            flyout.Items.Add(renameItem);

            var duplicateItem = new MenuFlyoutItem
            {
                Text = "Duplicate",
                Icon = new SymbolIcon(Symbol.Copy),
                Tag = tabId
            };
            duplicateItem.Click += DuplicateTabMenuItem_Click;
            flyout.Items.Add(duplicateItem);

            var closeOthersItem = new MenuFlyoutItem
            {
                Text = "Close Others",
                Icon = new SymbolIcon(Symbol.Cancel),
                Tag = tabId
            };
            closeOthersItem.Click += CloseOtherTabsMenuItem_Click;
            flyout.Items.Add(closeOthersItem);

            return flyout;
        }

        private async void RenameTabMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var tabId = GetTabIdFromMenuSender(sender);
            if (string.IsNullOrWhiteSpace(tabId))
            {
                return;
            }

            await RenameTabAsync(tabId);
        }

        private void DuplicateTabMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var tabId = GetTabIdFromMenuSender(sender);
            if (string.IsNullOrWhiteSpace(tabId))
            {
                return;
            }

            DuplicateTab(tabId);
        }

        private async void CloseOtherTabsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var tabId = GetTabIdFromMenuSender(sender);
            if (string.IsNullOrWhiteSpace(tabId))
            {
                return;
            }

            await CloseOtherTabsAsync(tabId);
        }

        private static string? GetTabIdFromMenuSender(object sender)
        {
            if (sender is FrameworkElement element && element.Tag is string tabId)
            {
                return tabId;
            }

            return null;
        }

        private async Task RenameTabAsync(string tabId)
        {
            if (!_tabStates.TryGetValue(tabId, out var state))
            {
                return;
            }

            var inputBox = new TextBox
            {
                Text = state.Header,
                PlaceholderText = "Nama tab"
            };

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Rename tab",
                Content = inputBox,
                PrimaryButtonText = "Simpan",
                CloseButtonText = "Batal",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            var renamed = inputBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(renamed))
            {
                return;
            }

            state.Header = renamed;
            state.IsHeaderManuallyEdited = true;
            _tabStates[tabId] = state;
            RefreshTabOptions();
        }

        private async Task CloseOtherTabsAsync(string keepTabId)
        {
            SaveActiveTabState();
            if (string.IsNullOrWhiteSpace(keepTabId) || !_tabStates.ContainsKey(keepTabId))
            {
                return;
            }

            var otherIds = _tabOrder
                .Where(id => !string.Equals(id, keepTabId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (otherIds.Count == 0)
            {
                return;
            }

            var hasUnsavedOthers = otherIds.Any(id =>
            {
                if (!_tabStates.TryGetValue(id, out var otherState))
                {
                    return false;
                }

                return otherState.IsDirty || HasTabContent(otherState);
            });

            if (hasUnsavedOthers)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "Tutup tab lain?",
                    Content = "Beberapa tab lain belum disimpan. Tetap tutup semua tab selain ini?",
                    PrimaryButtonText = "Tutup Semua",
                    CloseButtonText = "Batal",
                    DefaultButton = ContentDialogButton.Close
                };

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary)
                {
                    return;
                }
            }

            foreach (var id in otherIds)
            {
                _tabStates.Remove(id);
            }

            _tabOrder.Clear();
            _tabOrder.Add(keepTabId);
            SelectTab(keepTabId, saveCurrentState: false);
            SetMsgStatus("Tab lain ditutup.");
        }

        private async Task CloseTabAsync(string tabId)
        {
            if (string.IsNullOrWhiteSpace(tabId))
            {
                return;
            }

            if (_tabOrder.Count <= 1)
            {
                return;
            }

            var activeIndex = _tabOrder.FindIndex(x => string.Equals(x, tabId, StringComparison.OrdinalIgnoreCase));
            if (activeIndex < 0)
            {
                return;
            }

            if (!await ConfirmTabCloseAsync(tabId))
            {
                return;
            }

            var removingId = _tabOrder[activeIndex];
            _tabStates.Remove(tabId);
            _tabOrder.RemoveAt(activeIndex);

            if (string.Equals(_activeTabId, removingId, StringComparison.OrdinalIgnoreCase))
            {
                var nextIndex = Math.Max(0, activeIndex - 1);
                var nextId = _tabOrder[nextIndex];
                SelectTab(nextId, saveCurrentState: false);
            }
            else
            {
                RefreshTabOptions();
            }
        }

        private void FormTabsTabView_AddTabButtonClick(TabView sender, object args)
        {
            CreateAndSelectNewTab();
        }

        private async void FormTabsTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Tab is not TabViewItem item || item.Tag is not string tabId)
            {
                return;
            }

            if (string.Equals(tabId, _activeTabId, StringComparison.OrdinalIgnoreCase))
            {
                SaveActiveTabState();
            }

            await CloseTabAsync(tabId);
        }

        private void FormTabsTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSwitchingTabs)
            {
                return;
            }

            _isHandlingTabSelectionChanged = true;
            try
            {
                if (FormTabsTabView.SelectedItem is TabViewItem selected &&
                    selected.Tag is string selectedTabId)
                {
                    SelectTab(selectedTabId);
                }
            }
            finally
            {
                _isHandlingTabSelectionChanged = false;
            }

            if (_pendingTabRefresh)
            {
                QueueRefreshTabOptions();
            }
        }

        private async void LoadMsgButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DeveloperDiagnostics.LogInfo("CreateTtPage.LoadMsgButton_Click started.");
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".msg");

                if (App.MainAppWindow is null)
                {
                    MsgStatusTextBlock.Text = "Window utama tidak tersedia.";
                    DeveloperDiagnostics.LogError("CreateTtPage.LoadMsgButton_Click.MainWindowNull", null);
                    return;
                }

                var hWnd = WindowNative.GetWindowHandle(App.MainAppWindow);
                InitializeWithWindow.Initialize(picker, hWnd);
                var selectedFile = await picker.PickSingleFileAsync();
                if (selectedFile is null)
                {
                    DeveloperDiagnostics.LogInfo("MSG load canceled by user.");
                    return;
                }

                await LoadMsgFromStorageFileAsync(selectedFile);
            }
            catch (Exception ex)
            {
                var detail = ex.InnerException?.Message;
                var visibleError = "Gagal load .msg: " + ex.Message + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " | " + detail);
                SetMsgStatus(visibleError);
                DeveloperDiagnostics.LogInfo("CreateTtPage MSG catch visible error: " + visibleError);
                DeveloperDiagnostics.LogError("CreateTtPage.LoadMsgButton_Click", ex);
            }
        }

        private void EmailDropZoneBorder_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }

        private async void EmailDropZoneBorder_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (!e.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    SetMsgStatus("Gagal load .msg: file yang di-drop bukan file storage.");
                    return;
                }

                var items = await e.DataView.GetStorageItemsAsync();
                var file = items.OfType<StorageFile>()
                    .FirstOrDefault(x => string.Equals(Path.GetExtension(x.Name), ".msg", StringComparison.OrdinalIgnoreCase));

                if (file is null)
                {
                    SetMsgStatus("Gagal load .msg: file .msg tidak ditemukan di area drop.");
                    return;
                }

                await LoadMsgFromStorageFileAsync(file);
            }
            catch (Exception ex)
            {
                var detail = ex.InnerException?.Message;
                var visibleError = "Gagal load .msg: " + ex.Message + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " | " + detail);
                SetMsgStatus(visibleError);
                DeveloperDiagnostics.LogInfo("CreateTtPage MSG drop visible error: " + visibleError);
                DeveloperDiagnostics.LogError("CreateTtPage.EmailDropZoneBorder_Drop", ex);
            }
        }

        private void CoordinatePhotoDropZoneBorder_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }

        private async void CoordinatePhotoDropZoneBorder_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (!e.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    SetCoordinatePhotoStatus("Drop file gambar tidak valid.");
                    return;
                }

                var items = await e.DataView.GetStorageItemsAsync();
                var file = items
                    .OfType<StorageFile>()
                    .FirstOrDefault(IsImageFile);
                if (file is null)
                {
                    SetCoordinatePhotoStatus("File gambar tidak ditemukan pada drop.");
                    return;
                }

                await ProcessCoordinateImageFromStorageFileAsync(file);
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("CreateTtPage.CoordinatePhotoDropZoneBorder_Drop", ex);
                SetCoordinatePhotoStatus("Gagal proses drop foto: " + ex.Message);
            }
        }

        private async void LoadCoordinatePhotoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".bmp");
                picker.FileTypeFilter.Add(".webp");
                picker.FileTypeFilter.Add(".heic");

                if (App.MainAppWindow is null)
                {
                    SetCoordinatePhotoStatus("Window utama tidak tersedia.");
                    return;
                }

                var hWnd = WindowNative.GetWindowHandle(App.MainAppWindow);
                InitializeWithWindow.Initialize(picker, hWnd);
                var selectedFile = await picker.PickSingleFileAsync();
                if (selectedFile is null)
                {
                    return;
                }

                await ProcessCoordinateImageFromStorageFileAsync(selectedFile);
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("CreateTtPage.LoadCoordinatePhotoButton_Click", ex);
                SetCoordinatePhotoStatus("Gagal load foto: " + ex.Message);
            }
        }

        private async void PasteCoordinatePhotoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Bitmap))
                {
                    SetCoordinatePhotoStatus("Clipboard tidak berisi gambar.");
                    return;
                }

                var bitmap = await content.GetBitmapAsync();
                if (bitmap is null)
                {
                    SetCoordinatePhotoStatus("Gambar clipboard tidak ditemukan.");
                    return;
                }

                var bytes = await ReadAllBytesAsync(bitmap);
                if (bytes.Length == 0)
                {
                    SetCoordinatePhotoStatus("Gambar clipboard kosong.");
                    return;
                }

                await ProcessCoordinateImageBytesAsync(bytes, "image/png", "clipboard");
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("CreateTtPage.PasteCoordinatePhotoButton_Click", ex);
                SetCoordinatePhotoStatus("Gagal paste gambar: " + ex.Message);
            }
        }

        private async Task LoadMsgFromStorageFileAsync(StorageFile selectedFile)
        {
            DeveloperDiagnostics.LogInfo("MSG selected: " + selectedFile.Name);

            await using var msgStream = await OpenReadableMsgStreamAsync(selectedFile);
            await using var memory = new MemoryStream();
            await msgStream.CopyToAsync(memory);
            memory.Position = 0;
            DeveloperDiagnostics.LogInfo("MSG stream copied to memory, bytes=" + memory.Length);

            using var message = new Message(memory);
            FillFormFromMessage(message, selectedFile.Name);
            SetMsgStatus("Loaded: " + selectedFile.Name);
            DeveloperDiagnostics.LogInfo("MSG loaded: " + selectedFile.Name);
        }

        private async Task ProcessCoordinateImageFromStorageFileAsync(StorageFile file)
        {
            if (!IsImageFile(file))
            {
                SetCoordinatePhotoStatus("Format file tidak didukung.");
                return;
            }

            var bytes = await ReadAllBytesAsync(file);
            if (bytes.Length == 0)
            {
                SetCoordinatePhotoStatus("File gambar kosong.");
                return;
            }

            var mimeType = ToMimeType(file.FileType);
            await ProcessCoordinateImageBytesAsync(bytes, mimeType, file.Name);
        }

        private async Task ProcessCoordinateImageBytesAsync(byte[] bytes, string mimeType, string source)
        {
            _coordinateImageParseCts?.Cancel();
            _coordinateImageParseCts = new CancellationTokenSource();
            var token = _coordinateImageParseCts.Token;

            SetCoordinatePhotoStatus("AI parsing coordinate dari " + source + "...");
            try
            {
                var result = await GeminiCoordinateParserService.ExtractDmsCoordinateFromImageAsync(bytes, mimeType, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (!result.IsSuccess)
                {
                    SetCoordinatePhotoStatus(result.Message);
                    return;
                }

                CoordinateTextBox.Text = result.CoordinateDms;
                SetCoordinatePhotoStatus("Coordinate berhasil diparsing AI: " + result.CoordinateDms);
                UpdateTemplatePreview();
                MarkActiveTabDirty();
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("CreateTtPage.ProcessCoordinateImageBytesAsync", ex);
                SetCoordinatePhotoStatus("Gagal parsing AI coordinate: " + ex.Message);
            }
        }

        private static async Task<byte[]> ReadAllBytesAsync(StorageFile file)
        {
            var buffer = await FileIO.ReadBufferAsync(file);
            var bytes = new byte[buffer.Length];
            using var reader = DataReader.FromBuffer(buffer);
            reader.ReadBytes(bytes);
            return bytes;
        }

        private static async Task<byte[]> ReadAllBytesAsync(RandomAccessStreamReference streamReference)
        {
            using var stream = await streamReference.OpenReadAsync();
            var size = (uint)stream.Size;
            var bytes = new byte[size];
            using var reader = new DataReader(stream);
            await reader.LoadAsync(size);
            reader.ReadBytes(bytes);
            return bytes;
        }

        private static bool IsImageFile(StorageFile file)
        {
            return file is not null && ImageFileExtensions.Contains(file.FileType);
        }

        private static string ToMimeType(string extension)
        {
            return extension.ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".heic" => "image/heic",
                _ => "image/jpeg"
            };
        }

        private void SetCoordinatePhotoStatus(string text)
        {
            if (CoordinatePhotoStatusTextBlock is not null)
            {
                CoordinatePhotoStatusTextBlock.Text = text;
            }
        }

        private async void SaveLocalButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await SaveLocalInternalAsync(forceNew: false);
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("CreateTtPage.SaveLocalButton_Click", ex);
                SetMsgStatus("Gagal save local: " + ex.Message);
            }
        }

        private async void SaveAsLocalButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await SaveLocalInternalAsync(forceNew: true);
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("CreateTtPage.SaveAsLocalButton_Click", ex);
                SetMsgStatus("Gagal save as local: " + ex.Message);
            }
        }

        private async Task SaveLocalInternalAsync(bool forceNew)
        {
            SaveActiveTabState();
            var currentState = GetActiveTabStateOrDefault();
            var fallbackName = (!forceNew && !string.IsNullOrWhiteSpace(currentState.SavedRecordName))
                ? currentState.SavedRecordName
                : null;
            var draftName = ResolveDraftName(fallbackName);
            var record = BuildRecordFromCurrentForm(draftName);

            var isUpdate = false;
            if (!forceNew && !string.IsNullOrWhiteSpace(currentState.SavedRecordId))
            {
                record.Id = currentState.SavedRecordId;
                isUpdate = true;
            }

            await LocalFormStorageService.UpsertAsync(record);
            ApplySavedRecordReferenceToActiveTab(record);
            SaveNameTextBox.Text = record.Name;
            await RefreshSavedFormsAsync(record.Id);
            MarkActiveTabSaved();
            SetMsgStatus((isUpdate ? "Draft updated local: " : "Draft saved local: ") + record.Name);
        }

        private void LoadSavedButton_Click(object sender, RoutedEventArgs e)
        {
            if (SavedFormsComboBox.SelectedItem is not LocalFormRecord selected)
            {
                SetMsgStatus("Pilih draft terlebih dahulu.");
                return;
            }

            try
            {
                SaveActiveTabState();
                ApplySavedForm(selected);
                SaveActiveTabState();
                SetMsgStatus("Draft loaded: " + selected.Name);
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("CreateTtPage.LoadSavedButton_Click", ex);
                SetMsgStatus("Gagal load draft: " + ex.Message);
            }
        }

        private async void DeleteSavedButton_Click(object sender, RoutedEventArgs e)
        {
            if (SavedFormsComboBox.SelectedItem is not LocalFormRecord selected)
            {
                SetMsgStatus("Pilih draft yang ingin dihapus.");
                return;
            }

            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "Hapus draft?",
                    Content = "Draft '" + selected.Name + "' akan dihapus permanen dari penyimpanan lokal.",
                    PrimaryButtonText = "Hapus",
                    CloseButtonText = "Batal",
                    DefaultButton = ContentDialogButton.Close
                };

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary)
                {
                    return;
                }

                await LocalFormStorageService.DeleteAsync(selected.Id);
                ClearSavedReferenceIfMatches(selected.Id);
                await RefreshSavedFormsAsync();
                SetMsgStatus("Draft dihapus: " + selected.Name);
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("CreateTtPage.DeleteSavedButton_Click", ex);
                SetMsgStatus("Gagal hapus draft: " + ex.Message);
            }
        }

        private void SetMsgStatus(string text)
        {
            MsgStatusTextBlock.Text = text;
            if (text.StartsWith("Gagal load .msg:", StringComparison.OrdinalIgnoreCase))
            {
                DeveloperDiagnostics.LogInfo("CreateTtPage MSG status: " + text);
            }
        }

        private string ResolveDraftName(string? fallbackName = null)
        {
            var draftName = SaveNameTextBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(draftName))
            {
                return draftName;
            }

            if (!string.IsNullOrWhiteSpace(fallbackName))
            {
                return fallbackName;
            }

            return string.IsNullOrWhiteSpace(TitleTextBox.Text)
                ? "Draft " + DateTime.Now.ToString("dd-MM-yyyy HH:mm")
                : TitleTextBox.Text.Trim();
        }

        private LocalFormRecord BuildRecordFromCurrentForm(string draftName)
        {
            var occur = ParseOrFallbackDateTime(OccurTimeTextBox.Text, DateTimeOffset.Now);
            var dispatch = ParseOrFallbackDateTime(DispatchTimeTextBox.Text, DateTimeOffset.Now);

            return new LocalFormRecord
            {
                Name = draftName,
                SavedAt = DateTimeOffset.Now,
                TtIoh = GetEffectiveTtIoh(),
                Title = TitleTextBox.Text?.Trim() ?? string.Empty,
                OccurDateTime = occur,
                DispatchDateTime = dispatch,
                StatusLink = GetSelectedStatusLink(),
                Pic = PicAutoSuggestBox.Text?.Trim() ?? string.Empty,
                RootCause = RootCauseTextBox.Text?.Trim() ?? string.Empty,
                CutPoint = CutPointTextBox.Text?.Trim() ?? string.Empty,
                ShowSegmentRoute = ShowSegmentRouteToggleSwitch.IsOn,
                ShowSystemKey = ShowSystemKeyToggleSwitch.IsOn,
                SegmentRoute = GetSelectedSegmentRoute(),
                SystemKey = SystemKeyTextBox.Text?.Trim() ?? string.Empty,
                Coordinate = CoordinateTextBox.Text?.Trim() ?? string.Empty,
                UpdateProgress = UpdateProgressTextBox.Text?.Trim() ?? string.Empty,
                ImpactList = CloneImpactList(_impactListItems)
            };
        }

        private FormTabState GetActiveTabStateOrDefault()
        {
            if (!string.IsNullOrWhiteSpace(_activeTabId) &&
                _tabStates.TryGetValue(_activeTabId, out var state))
            {
                return state;
            }

            return new FormTabState();
        }

        private void ApplySavedRecordReferenceToActiveTab(LocalFormRecord record)
        {
            if (string.IsNullOrWhiteSpace(_activeTabId))
            {
                return;
            }

            if (_tabStates.TryGetValue(_activeTabId, out var state))
            {
                state.SavedRecordId = record.Id;
                state.SavedRecordName = record.Name;
                state.DraftName = record.Name;
                _tabStates[_activeTabId] = state;
            }
        }

        private void ClearSavedReferenceIfMatches(string savedRecordId)
        {
            if (string.IsNullOrWhiteSpace(savedRecordId))
            {
                return;
            }

            foreach (var tabId in _tabOrder)
            {
                if (!_tabStates.TryGetValue(tabId, out var state))
                {
                    continue;
                }

                if (string.Equals(state.SavedRecordId, savedRecordId, StringComparison.OrdinalIgnoreCase))
                {
                    state.SavedRecordId = string.Empty;
                    state.SavedRecordName = string.Empty;
                    _tabStates[tabId] = state;
                }
            }
        }

        private static async System.Threading.Tasks.Task<Stream> OpenReadableMsgStreamAsync(Windows.Storage.StorageFile selectedFile)
        {
            if (!string.IsNullOrWhiteSpace(selectedFile.Path) && File.Exists(selectedFile.Path))
            {
                try
                {
                    return new FileStream(
                        selectedFile.Path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                }
                catch (Exception ex)
                {
                    DeveloperDiagnostics.LogError("CreateTtPage.OpenReadableMsgStreamAsync.FileStreamFallback", ex);
                }
            }

            return await selectedFile.OpenStreamForReadAsync();
        }

        private void FillFormFromMessage(Message message, string fileName)
        {
            var body = GetMessageBody(message);
            var subject = message.Subject ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(subject))
            {
                TitleTextBox.Text = subject.Trim();
                TryAutoParseTtIoh(subject);
            }

            var ttIohFromBody = ExtractLineValue(body, "TT IOH");
            if (string.IsNullOrWhiteSpace(ttIohFromBody))
            {
                ttIohFromBody = ExtractLineValue(body, "TT");
            }

            var parsedTtIoh = NormalizeTtIoh(ttIohFromBody);
            if (string.IsNullOrWhiteSpace(parsedTtIoh))
            {
                parsedTtIoh = ExtractTtIoh(subject);
            }

            if (!string.IsNullOrWhiteSpace(parsedTtIoh))
            {
                TtIohTextBox.Text = parsedTtIoh;
            }

            var occurLine = ExtractLineValue(body, "Occur Time");
            if (TryParseEmailDate(occurLine, out var occurDateTime))
            {
                OccurTimeTextBox.Text = FormatDateTime(occurDateTime);
            }

            var dispatchDate = message.ReceivedOn ?? message.SentOn;
            if (dispatchDate.HasValue)
            {
                DispatchTimeTextBox.Text = FormatDateTime(dispatchDate.Value);
            }

            ApplyStatusLinkSelection("Open");
            RootCauseTextBox.Text = string.Empty;
            CutPointTextBox.Text = string.Empty;
            PicAutoSuggestBox.Text = string.Empty;
            PicAutoSuggestBox.ItemsSource = null;
            _ = EnsureAllPicOptionsLoadedAsync();

            var segment = ExtractLineValue(body, "Segment");
            var systemKey = ExtractLineValue(body, "System Key");

            if (!string.IsNullOrWhiteSpace(segment))
            {
                SegmentRouteAutoSuggestBox.Tag = segment;
            }

            if (!string.IsNullOrWhiteSpace(systemKey))
            {
                SystemKeyTextBox.Text = systemKey;
            }

            UpdateProgressTextBox.Text = string.Empty;
            _impactListItems.Clear();
            RefreshImpactListUi();
            UpdateTemplatePreview();
        }

        private static string GetMessageBody(Message message)
        {
            if (!string.IsNullOrWhiteSpace(message.BodyText))
            {
                return message.BodyText;
            }

            var html = message.BodyHtml ?? string.Empty;
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            var noTag = Regex.Replace(html, "<.*?>", " ");
            return WebUtility.HtmlDecode(noTag);
        }

        private static string ExtractLineValue(string body, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            var pattern = @"^\s*" + Regex.Escape(fieldName) + @"\s*[:=]\s*(.+?)\s*$";
            var match = Regex.Match(body, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        private static bool TryParseEmailDate(string value, out DateTimeOffset result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var formats = new[]
            {
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-dd HH:mm",
                "dd-MM-yyyy HH:mm:ss",
                "dd-MM-yyyy HH:mm"
            };

            foreach (var format in formats)
            {
                if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
                {
                    result = parsed;
                    return true;
                }
            }

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var fallback))
            {
                result = fallback;
                return true;
            }

            return false;
        }

        private void InputChanged(object sender, TextChangedEventArgs e)
        {
            UpdateTemplatePreview();
            if (_isApplyingSavedForm)
            {
                return;
            }

            if (ReferenceEquals(sender, TitleTextBox))
            {
                TryAutoParseTtIoh(TitleTextBox.Text);
                UpdateActiveTabHeaderFromCurrentForm();
            }

            if (ReferenceEquals(sender, TtIohTextBox))
            {
                NormalizeTtIohInput();
                UpdateActiveTabHeaderFromCurrentForm();
            }

            if (ReferenceEquals(sender, SystemKeyTextBox))
            {
                _ = TryAutoFillSegmentRouteFromSystemKeyAsync();
            }

            MarkActiveTabDirty();
        }

        private void TryAutoParseTtIoh(string? source)
        {
            if (_isApplyingSavedForm || _isUpdatingTtIohText)
            {
                return;
            }

            var parsed = ExtractTtIoh(source);
            if (string.IsNullOrWhiteSpace(parsed))
            {
                return;
            }

            if (string.Equals(TtIohTextBox.Text?.Trim(), parsed, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _isUpdatingTtIohText = true;
            try
            {
                TtIohTextBox.Text = parsed;
            }
            finally
            {
                _isUpdatingTtIohText = false;
            }
        }

        private void NormalizeTtIohInput()
        {
            if (_isUpdatingTtIohText)
            {
                return;
            }

            var normalized = NormalizeTtIoh(TtIohTextBox.Text);
            var current = TtIohTextBox.Text?.Trim() ?? string.Empty;
            if (string.Equals(current, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _isUpdatingTtIohText = true;
            try
            {
                TtIohTextBox.Text = normalized;
            }
            finally
            {
                _isUpdatingTtIohText = false;
            }
        }

        private string GetEffectiveTtIoh()
        {
            var normalized = NormalizeTtIoh(TtIohTextBox.Text);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }

            return ExtractTtIoh(TitleTextBox.Text);
        }

        private static string NormalizeTtIoh(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim().ToUpperInvariant();
            return TtIohRegex.IsMatch(trimmed) ? trimmed : string.Empty;
        }

        private static string ExtractTtIoh(string? source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return string.Empty;
            }

            var match = TtIohRegex.Match(source);
            return match.Success ? match.Value.ToUpperInvariant() : string.Empty;
        }

        private void DateTimeTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox textBox)
            {
                return;
            }

            var input = textBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            if (TryParseInputDateTime(input, out var parsed))
            {
                var normalized = FormatDateTime(parsed);
                if (!string.Equals(textBox.Text, normalized, StringComparison.Ordinal))
                {
                    textBox.Text = normalized;
                }
            }
        }

        private void PicAutoSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (_isPopulatingSegmentOptions)
            {
                return;
            }

            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                ApplyPicFilter(sender.Text, null, false);
                sender.IsSuggestionListOpen = true;
                UpdateTemplatePreview();
                MarkActiveTabDirty();
            }
        }

        private void PicAutoSuggestBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            if (args.SelectedItem is string selected)
            {
                sender.Text = selected;
                UpdateTemplatePreview();
                MarkActiveTabDirty();
            }
        }

        private void PicAutoSuggestBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (args.ChosenSuggestion is string chosen)
            {
                sender.Text = chosen;
                UpdateTemplatePreview();
                return;
            }

            if (string.IsNullOrWhiteSpace(sender.Text))
            {
                return;
            }

            var exact = _allPicOptions.FirstOrDefault(x => string.Equals(x, sender.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
            {
                sender.Text = exact;
            }

            UpdateTemplatePreview();
            MarkActiveTabDirty();
        }

        private async void PicAutoSuggestBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not AutoSuggestBox box)
            {
                return;
            }

            await EnsureAllPicOptionsLoadedAsync();
            ApplyPicFilter(box.Text, null, false);
            box.IsSuggestionListOpen = true;
        }

        private void SegmentRouteAutoSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (_isPopulatingSegmentOptions)
            {
                return;
            }

            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                ApplySegmentRouteFilter(sender.Text, null, false);
                sender.IsSuggestionListOpen = true;
                MarkActiveTabDirty();
            }
        }

        private void SegmentRouteAutoSuggestBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            if (args.SelectedItem is string selected)
            {
                sender.Text = selected;
                SyncPicFromSelectedSegment();
                UpdateTemplatePreview();
                MarkActiveTabDirty();
            }
        }

        private void SegmentRouteAutoSuggestBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (args.ChosenSuggestion is string chosen)
            {
                sender.Text = chosen;
            }

            if (string.IsNullOrWhiteSpace(sender.Text))
            {
                return;
            }

            var exact = _allSegmentRoutes.FirstOrDefault(x => string.Equals(x, sender.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
            {
                sender.Text = exact;
            }

            SyncPicFromSelectedSegment();
            UpdateTemplatePreview();
            MarkActiveTabDirty();
        }

        private void SegmentRouteAutoSuggestBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not AutoSuggestBox box)
            {
                return;
            }

            if (!HasSegmentItems())
            {
                _ = TryAutoFillSegmentRouteFromSystemKeyAsync();
                return;
            }

            ApplySegmentRouteFilter(box.Text, null, false);
            box.IsSuggestionListOpen = true;
        }

        private void PreviewOptionChanged(object sender, RoutedEventArgs e)
        {
            UpdateTemplatePreview();
            MarkActiveTabDirty();
        }

        private void StatusLinkComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplyingSavedForm)
            {
                return;
            }

            UpdateTemplatePreview();
            MarkActiveTabDirty();
        }

        private void AddImpactListButton_Click(object sender, RoutedEventArgs e)
        {
            _impactListItems.Add(new ImpactListItem
            {
                Impact = string.Empty,
                StatusLink = "Down"
            });

            RefreshImpactListUi();
            UpdateTemplatePreview();
            MarkActiveTabDirty();
        }

        private void RefreshImpactListUi()
        {
            if (ImpactListItemsHost is null)
            {
                return;
            }

            ImpactListItemsHost.Children.Clear();
            for (var i = 0; i < _impactListItems.Count; i++)
            {
                var rowItem = _impactListItems[i];
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var impactTextBox = new TextBox
                {
                    PlaceholderText = "Contoh Impact",
                    Text = rowItem.Impact ?? string.Empty,
                    Style = (Style)Application.Current.Resources["ShellInputTextBoxStyle"]
                };
                impactTextBox.TextChanged += (_, _) =>
                {
                    rowItem.Impact = impactTextBox.Text?.Trim() ?? string.Empty;
                    UpdateTemplatePreview();
                    if (!_isApplyingSavedForm)
                    {
                        MarkActiveTabDirty();
                    }
                };
                Grid.SetColumn(impactTextBox, 0);
                row.Children.Add(impactTextBox);

                var statusComboBox = new ComboBox
                {
                    MinWidth = 120,
                    PlaceholderText = "Status",
                    ItemsSource = ImpactStatusOptions,
                    Style = (Style)Application.Current.Resources["ShellInputComboBoxStyle"]
                };
                statusComboBox.SelectedItem = ToImpactStatusOptionLabel(NormalizeImpactStatus(rowItem.StatusLink));
                statusComboBox.SelectionChanged += (_, _) =>
                {
                    rowItem.StatusLink = NormalizeImpactStatus(statusComboBox.SelectedItem as string ?? statusComboBox.Text);
                    UpdateTemplatePreview();
                    if (!_isApplyingSavedForm)
                    {
                        MarkActiveTabDirty();
                    }
                };
                Grid.SetColumn(statusComboBox, 1);
                row.Children.Add(statusComboBox);

                var removeButton = new Button
                {
                    Content = "Hapus",
                    Style = (Style)Application.Current.Resources["ShellSecondaryButtonStyle"]
                };
                removeButton.Click += (_, _) =>
                {
                    _impactListItems.Remove(rowItem);
                    RefreshImpactListUi();
                    UpdateTemplatePreview();
                    if (!_isApplyingSavedForm)
                    {
                        MarkActiveTabDirty();
                    }
                };
                Grid.SetColumn(removeButton, 2);
                row.Children.Add(removeButton);

                ImpactListItemsHost.Children.Add(row);
            }

            if (ImpactListEmptyHintTextBlock is not null)
            {
                ImpactListEmptyHintTextBlock.Visibility = _impactListItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private List<string> BuildImpactListPreviewLines()
        {
            var lines = new List<string>();
            foreach (var impact in _impactListItems)
            {
                var text = impact?.Impact?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var status = NormalizeImpactStatus(impact?.StatusLink);
                var emoji = GetStatusEmoji(status);
                lines.Add(string.IsNullOrWhiteSpace(emoji) ? "- " + text : "- " + text + " " + emoji);
            }

            return lines;
        }

        private static List<ImpactListItem> CloneImpactList(IEnumerable<ImpactListItem>? source)
        {
            if (source is null)
            {
                return new List<ImpactListItem>();
            }

            return source
                .Where(item => item is not null)
                .Select(item => new ImpactListItem
                {
                    Impact = item.Impact?.Trim() ?? string.Empty,
                    StatusLink = NormalizeImpactStatus(item.StatusLink)
                })
                .ToList();
        }

        private static string NormalizeImpactStatus(string? value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return "Down";
            }

            if (text.Contains("down", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("open", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("❌", StringComparison.Ordinal))
            {
                return "Down";
            }

            if (text.Contains("up", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("close", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("✅", StringComparison.Ordinal))
            {
                return "Up";
            }

            if (text.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("⛔", StringComparison.Ordinal))
            {
                return "Cancel";
            }

            return "Down";
        }

        private static string ToImpactStatusOptionLabel(string normalizedStatus)
        {
            var normalized = NormalizeImpactStatus(normalizedStatus);
            if (string.Equals(normalized, "Up", StringComparison.OrdinalIgnoreCase))
            {
                return "Up ✅";
            }

            if (string.Equals(normalized, "Cancel", StringComparison.OrdinalIgnoreCase))
            {
                return "Cancel ⛔";
            }

            return "Down ❌";
        }

        private static string FormatMainStatusLinkForPreview(string? value)
        {
            var normalized = NormalizeStatusLink(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            var emoji = GetStatusEmoji(normalized);
            return string.IsNullOrWhiteSpace(emoji) ? normalized : normalized + " " + emoji;
        }

        private static string GetStatusEmoji(string? value)
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            if (string.Equals(normalized, "Down", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Open", StringComparison.OrdinalIgnoreCase))
            {
                return "❌";
            }

            if (string.Equals(normalized, "Up", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Closed", StringComparison.OrdinalIgnoreCase))
            {
                return "✅";
            }

            if (string.Equals(normalized, "Cancel", StringComparison.OrdinalIgnoreCase))
            {
                return "⛔";
            }

            return string.Empty;
        }

        private void UpdateTemplatePreview()
        {
            if (TemplatePreviewTextBox is null)
            {
                return;
            }

            var title = string.IsNullOrWhiteSpace(TitleTextBox?.Text) ? "Judul TT" : TitleTextBox.Text.Trim();
            var ttIoh = NormalizeTtIoh(TtIohTextBox?.Text);
            var statusLink = GetSelectedStatusLink();
            var statusLinkDisplay = FormatMainStatusLinkForPreview(statusLink);
            var occurTime = GetDateTimePreviewValue(OccurTimeTextBox?.Text, DateTimeOffset.Now);
            var dispatchTime = GetDateTimePreviewValue(DispatchTimeTextBox?.Text, DateTimeOffset.Now);
            var pic = PicAutoSuggestBox?.Text?.Trim() ?? string.Empty;
            var rootCause = RootCauseTextBox?.Text?.Trim() ?? string.Empty;
            var cutPoint = CutPointTextBox?.Text?.Trim() ?? string.Empty;
            var segmentRoute = GetSelectedSegmentRoute();
            var systemKey = SystemKeyTextBox?.Text?.Trim() ?? string.Empty;
            var coordinate = CoordinateTextBox?.Text?.Trim() ?? string.Empty;
            var updateProgress = UpdateProgressTextBox?.Text?.Trim() ?? string.Empty;
            var showSegmentRoute = ShowSegmentRouteToggleSwitch?.IsOn ?? true;
            var showSystemKey = ShowSystemKeyToggleSwitch?.IsOn ?? true;
            var impactLines = BuildImpactListPreviewLines();

            var preview = "*" + title + "*" + Environment.NewLine +
                (string.IsNullOrWhiteSpace(ttIoh) ? string.Empty : "TT IOH = " + ttIoh + Environment.NewLine) +
                (impactLines.Count == 0
                    ? string.Empty
                    : Environment.NewLine + "Impact List :" + Environment.NewLine + string.Join(Environment.NewLine, impactLines) + Environment.NewLine + Environment.NewLine) +
                (string.IsNullOrWhiteSpace(statusLinkDisplay) ? string.Empty : "Status Link = " + statusLinkDisplay + Environment.NewLine) +
                "Occur Time = " + occurTime + Environment.NewLine +
                "Dispacth Time = " + dispatchTime + Environment.NewLine +
                "PIC = " + pic + Environment.NewLine +
                "Rootcause = " + rootCause + Environment.NewLine +
                "Cut Point = " + cutPoint + Environment.NewLine +
                (!showSegmentRoute || string.IsNullOrWhiteSpace(segmentRoute) ? string.Empty : "Segment Route = " + segmentRoute + Environment.NewLine) +
                (!showSystemKey || string.IsNullOrWhiteSpace(systemKey) ? string.Empty : "System Key = " + systemKey + Environment.NewLine) +
                (string.IsNullOrWhiteSpace(coordinate) ? string.Empty : "Coordinate = " + coordinate + Environment.NewLine) +
                "Update Progress" + Environment.NewLine +
                updateProgress;

            TemplatePreviewTextBox.Text = preview;
        }

        private static string FormatDateTime(DateTimeOffset value)
        {
            return value.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);
        }

        private static DateTimeOffset ParseOrFallbackDateTime(string? text, DateTimeOffset fallback)
        {
            if (TryParseInputDateTime(text, out var parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private static string GetDateTimePreviewValue(string? text, DateTimeOffset fallback)
        {
            var trimmed = text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return FormatDateTime(fallback);
            }

            if (TryParseInputDateTime(trimmed, out var parsed))
            {
                return FormatDateTime(parsed);
            }

            return trimmed;
        }

        private static bool TryParseInputDateTime(string? text, out DateTimeOffset result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (DateTime.TryParseExact(
                    text.Trim(),
                    DateTimeInputFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var parsedExact))
            {
                result = new DateTimeOffset(parsedExact);
                return true;
            }

            if (DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsedFallback))
            {
                result = new DateTimeOffset(parsedFallback);
                return true;
            }

            return false;
        }

        private async Task TryAutoFillSegmentRouteFromSystemKeyAsync(bool keepExistingPic = false, bool keepExistingSegment = false)
        {
            _systemKeyLookupCts?.Cancel();
            _systemKeyLookupCts = new CancellationTokenSource();
            var token = _systemKeyLookupCts.Token;

            try
            {
                await Task.Delay(300, token);
                var systemKeyInput = SystemKeyTextBox.Text?.Trim() ?? string.Empty;
                SetMsgStatus(string.IsNullOrWhiteSpace(systemKeyInput)
                    ? "Memuat semua Segment Route..."
                    : "Mencari Segment Route dari System Key...");
                var lookup = await DatabaseLinkLookupService.FindSegmentLookupBySystemKeyAsync(systemKeyInput);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (lookup.Segments.Count == 0)
                {
                    _isPopulatingSegmentOptions = true;
                    SegmentRouteAutoSuggestBox.ItemsSource = null;
                    if (!keepExistingSegment)
                    {
                        SegmentRouteAutoSuggestBox.Text = string.Empty;
                    }
                    SegmentRouteAutoSuggestBox.Tag = null;
                    _isPopulatingSegmentOptions = false;
                    _picBySegmentRoute = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                    _allSegmentRoutes = new List<string>();
                    PicAutoSuggestBox.ItemsSource = null;
                    if (!keepExistingPic)
                    {
                        PicAutoSuggestBox.Text = string.Empty;
                    }
                    DeveloperDiagnostics.LogInfo("System Key lookup: no match for [" + systemKeyInput + "].");
                    SetMsgStatus("Segment Route tidak ditemukan untuk System Key tersebut.");
                    return;
                }

                _picBySegmentRoute = new Dictionary<string, IReadOnlyList<string>>(lookup.PicBySegment, StringComparer.OrdinalIgnoreCase);
                _allSegmentRoutes = lookup.Segments.ToList();
                var pendingSegment = SegmentRouteAutoSuggestBox.Tag as string;
                var currentSegment = GetSelectedSegmentRoute();
                var selectedSegment = string.Empty;
                if (keepExistingSegment && !string.IsNullOrWhiteSpace(currentSegment))
                {
                    selectedSegment = currentSegment;
                }
                else if (!string.IsNullOrWhiteSpace(pendingSegment) &&
                         lookup.Segments.Any(x => string.Equals(x, pendingSegment, StringComparison.OrdinalIgnoreCase)))
                {
                    selectedSegment = lookup.Segments.First(x => string.Equals(x, pendingSegment, StringComparison.OrdinalIgnoreCase));
                }
                else if (!string.IsNullOrWhiteSpace(currentSegment) &&
                         lookup.Segments.Any(x => string.Equals(x, currentSegment, StringComparison.OrdinalIgnoreCase)))
                {
                    selectedSegment = lookup.Segments.First(x => string.Equals(x, currentSegment, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    selectedSegment = lookup.Segments[0];
                }

                SegmentRouteAutoSuggestBox.Tag = null;
                ApplySegmentRouteFilter(null, selectedSegment, true);
                if (keepExistingSegment && !string.IsNullOrWhiteSpace(currentSegment))
                {
                    SegmentRouteAutoSuggestBox.Text = currentSegment;
                }

                SyncPicFromSelectedSegment(keepExistingPic);
                UpdateTemplatePreview();
                var picCount = _picBySegmentRoute.TryGetValue(selectedSegment, out var items) ? items.Count : 0;
                DeveloperDiagnostics.LogInfo("System Key lookup matched " + lookup.Segments.Count + " segment route(s) and " + picCount + " PIC entry.");
                SetMsgStatus(string.IsNullOrWhiteSpace(systemKeyInput)
                    ? "Loaded: " + lookup.Segments.Count + " Segment Route."
                    : "Loaded: " + lookup.Segments.Count + " Segment Route untuk System Key.");
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("CreateTtPage.TryAutoFillSegmentRouteFromSystemKeyAsync", ex);
            }
        }

        private string GetSelectedSegmentRoute()
        {
            if (!string.IsNullOrWhiteSpace(SegmentRouteAutoSuggestBox?.Text))
            {
                return SegmentRouteAutoSuggestBox.Text.Trim();
            }

            return string.Empty;
        }

        private void SyncPicFromSelectedSegment(bool keepExistingPic = false)
        {
            var currentPic = PicAutoSuggestBox.Text?.Trim() ?? string.Empty;
            if (keepExistingPic && !string.IsNullOrWhiteSpace(currentPic))
            {
                return;
            }

            var selectedSegment = GetSelectedSegmentRoute();
            if (string.IsNullOrWhiteSpace(selectedSegment))
            {
                if (!keepExistingPic)
                {
                    PicAutoSuggestBox.Text = string.Empty;
                }
                return;
            }

            if (!_picBySegmentRoute.TryGetValue(selectedSegment, out var picItems) || picItems.Count == 0)
            {
                if (!keepExistingPic)
                {
                    PicAutoSuggestBox.Text = string.Empty;
                }
                return;
            }

            ApplyPicFilter(null, picItems[0], true);
        }

        private FormTabState CaptureFormState()
        {
            var occur = ParseOrFallbackDateTime(OccurTimeTextBox.Text, DateTimeOffset.Now);
            var dispatch = ParseOrFallbackDateTime(DispatchTimeTextBox.Text, DateTimeOffset.Now);
            var currentState = GetActiveTabStateOrDefault();

            return new FormTabState
            {
                TtIoh = GetEffectiveTtIoh(),
                Title = TitleTextBox.Text?.Trim() ?? string.Empty,
                OccurDateTime = occur,
                DispatchDateTime = dispatch,
                StatusLink = GetSelectedStatusLink(),
                Pic = PicAutoSuggestBox.Text?.Trim() ?? string.Empty,
                RootCause = RootCauseTextBox.Text?.Trim() ?? string.Empty,
                CutPoint = CutPointTextBox.Text?.Trim() ?? string.Empty,
                ShowSegmentRoute = ShowSegmentRouteToggleSwitch.IsOn,
                ShowSystemKey = ShowSystemKeyToggleSwitch.IsOn,
                SegmentRoute = GetSelectedSegmentRoute(),
                SystemKey = SystemKeyTextBox.Text?.Trim() ?? string.Empty,
                Coordinate = CoordinateTextBox.Text?.Trim() ?? string.Empty,
                UpdateProgress = UpdateProgressTextBox.Text?.Trim() ?? string.Empty,
                ImpactList = CloneImpactList(_impactListItems),
                DraftName = SaveNameTextBox.Text?.Trim() ?? string.Empty,
                SavedRecordId = currentState.SavedRecordId,
                SavedRecordName = currentState.SavedRecordName
            };
        }

        private void ApplyFormState(FormTabState state)
        {
            _isApplyingSavedForm = true;
            try
            {
                TtIohTextBox.Text = string.IsNullOrWhiteSpace(state.TtIoh)
                    ? ExtractTtIoh(state.Title)
                    : NormalizeTtIoh(state.TtIoh);
                TitleTextBox.Text = state.Title;
                OccurTimeTextBox.Text = FormatDateTime(state.OccurDateTime);
                DispatchTimeTextBox.Text = FormatDateTime(state.DispatchDateTime);
                ApplyStatusLinkSelection(state.StatusLink);
                PicAutoSuggestBox.Text = state.Pic;
                RootCauseTextBox.Text = state.RootCause;
                CutPointTextBox.Text = state.CutPoint;
                ShowSegmentRouteToggleSwitch.IsOn = state.ShowSegmentRoute;
                ShowSystemKeyToggleSwitch.IsOn = state.ShowSystemKey;
                SegmentRouteAutoSuggestBox.Tag = state.SegmentRoute;
                SegmentRouteAutoSuggestBox.Text = state.SegmentRoute;
                SystemKeyTextBox.Text = state.SystemKey;
                CoordinateTextBox.Text = state.Coordinate;
                UpdateProgressTextBox.Text = state.UpdateProgress;
                SaveNameTextBox.Text = state.DraftName;
                _impactListItems.Clear();
                _impactListItems.AddRange(CloneImpactList(state.ImpactList));
                RefreshImpactListUi();
            }
            finally
            {
                _isApplyingSavedForm = false;
            }

            UpdateTemplatePreview();
            _ = TryAutoFillSegmentRouteFromSystemKeyAsync(keepExistingPic: true, keepExistingSegment: true);
        }

        private void UpdateActiveTabHeaderFromCurrentForm()
        {
            if (string.IsNullOrWhiteSpace(_activeTabId))
            {
                return;
            }

            if (_tabStates.TryGetValue(_activeTabId, out var state))
            {
                state.TtIoh = GetEffectiveTtIoh();
                state.Title = TitleTextBox.Text?.Trim() ?? string.Empty;
                if (!state.IsHeaderManuallyEdited)
                {
                    state.Header = ResolveTabHeader(state.Header, state.TtIoh, state.Title);
                }

                _tabStates[_activeTabId] = state;
                UpdateActiveTabHeaderFromState(state);
            }
        }

        private void UpdateActiveTabHeaderFromState(FormTabState state)
        {
            if (string.IsNullOrWhiteSpace(state.TabId))
            {
                return;
            }

            if (_tabStates.TryGetValue(state.TabId, out var existing))
            {
                if (!existing.IsHeaderManuallyEdited)
                {
                    existing.Header = ResolveTabHeader(existing.Header, existing.TtIoh, existing.Title);
                }

                _tabStates[state.TabId] = existing;
                state = existing;
            }

            RefreshTabOptions();
        }

        private static string ResolveTabHeader(string currentHeader, string ttIoh, string title)
        {
            if (!string.IsNullOrWhiteSpace(ttIoh))
            {
                return ttIoh;
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                var parsed = ExtractTtIoh(title);
                if (!string.IsNullOrWhiteSpace(parsed))
                {
                    return parsed;
                }
            }

            return string.IsNullOrWhiteSpace(currentHeader) ? "Draft" : currentHeader;
        }

        private bool HasSegmentItems()
        {
            return _allSegmentRoutes.Count > 0;
        }

        private void ApplySegmentRouteFilter(string? keyword = null, string? preferredSelection = null, bool applySelectionToText = true)
        {
            IEnumerable<string> filtered = _allSegmentRoutes;
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                filtered = filtered.Where(x => x.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            }

            var list = filtered.ToList();
            var currentSelection = GetSelectedSegmentRoute();
            var nextSelection = string.Empty;

            if (!string.IsNullOrWhiteSpace(preferredSelection) &&
                list.Any(x => string.Equals(x, preferredSelection, StringComparison.OrdinalIgnoreCase)))
            {
                nextSelection = list.First(x => string.Equals(x, preferredSelection, StringComparison.OrdinalIgnoreCase));
            }
            else if (!string.IsNullOrWhiteSpace(currentSelection) &&
                     list.Any(x => string.Equals(x, currentSelection, StringComparison.OrdinalIgnoreCase)))
            {
                nextSelection = list.First(x => string.Equals(x, currentSelection, StringComparison.OrdinalIgnoreCase));
            }
            else if (list.Count > 0)
            {
                nextSelection = list[0];
            }

            _isPopulatingSegmentOptions = true;
            SegmentRouteAutoSuggestBox.ItemsSource = list;
            if (applySelectionToText && !string.IsNullOrWhiteSpace(nextSelection))
            {
                SegmentRouteAutoSuggestBox.Text = nextSelection;
            }
            _isPopulatingSegmentOptions = false;
        }

        private async Task EnsureAllPicOptionsLoadedAsync()
        {
            if (_allPicOptionsLoaded)
            {
                return;
            }

            try
            {
                var allPic = await DatabaseLinkLookupService.FindAllPicDisplaysAsync();
                _allPicOptions = allPic.ToList();
                _allPicOptionsLoaded = true;
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("CreateTtPage.EnsureAllPicOptionsLoadedAsync", ex);
            }
        }

        private void ApplyPicFilter(string? keyword = null, string? preferredSelection = null, bool applySelectionToText = false)
        {
            var picBox = PicAutoSuggestBox;
            if (picBox is null)
            {
                return;
            }

            IEnumerable<string> filtered = _allPicOptions;
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                filtered = filtered.Where(x => x.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            }

            var list = filtered.ToList();
            var currentSelection = PicAutoSuggestBox?.Text?.Trim() ?? string.Empty;
            var nextSelection = string.Empty;

            if (!string.IsNullOrWhiteSpace(preferredSelection) &&
                list.Any(x => string.Equals(x, preferredSelection, StringComparison.OrdinalIgnoreCase)))
            {
                nextSelection = list.First(x => string.Equals(x, preferredSelection, StringComparison.OrdinalIgnoreCase));
            }
            else if (!string.IsNullOrWhiteSpace(currentSelection) &&
                     list.Any(x => string.Equals(x, currentSelection, StringComparison.OrdinalIgnoreCase)))
            {
                nextSelection = list.First(x => string.Equals(x, currentSelection, StringComparison.OrdinalIgnoreCase));
            }
            else if (list.Count > 0)
            {
                nextSelection = list[0];
            }

            _isPopulatingSegmentOptions = true;
            picBox.ItemsSource = list;
            if (applySelectionToText && !string.IsNullOrWhiteSpace(nextSelection))
            {
                picBox.Text = nextSelection;
            }
            _isPopulatingSegmentOptions = false;
        }

        private async Task EnsureStatusLinkOptionsLoadedAsync()
        {
            if (_statusLinkOptionsLoaded)
            {
                return;
            }

            try
            {
                var options = await StatusLinkLookupService.GetStatusLinkOptionsAsync();
                _statusLinkOptions = options.ToList();
                if (_statusLinkOptions.Count == 0)
                {
                    _statusLinkOptions = new List<string> { "Open", "Closed", "Cancel" };
                }

                StatusLinkComboBox.ItemsSource = _statusLinkOptions;
                _statusLinkOptionsLoaded = true;

                var desired = string.IsNullOrWhiteSpace(_pendingStatusLink) ? "Open" : _pendingStatusLink;
                _pendingStatusLink = string.Empty;
                ApplyStatusLinkSelection(desired);
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("CreateTtPage.EnsureStatusLinkOptionsLoadedAsync", ex);
                _statusLinkOptions = new List<string> { "Open", "Closed", "Cancel" };
                StatusLinkComboBox.ItemsSource = _statusLinkOptions;
                _statusLinkOptionsLoaded = true;
                ApplyStatusLinkSelection("Open");
            }
        }

        private string GetSelectedStatusLink()
        {
            if (StatusLinkComboBox.SelectedItem is string selected)
            {
                return NormalizeStatusLink(selected);
            }

            return NormalizeStatusLink(StatusLinkComboBox.Text);
        }

        private void ApplyStatusLinkSelection(string? value)
        {
            var normalized = NormalizeStatusLink(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = "Open";
            }

            if (!_statusLinkOptionsLoaded)
            {
                _pendingStatusLink = normalized;
                _ = EnsureStatusLinkOptionsLoadedAsync();
                return;
            }

            if (!_statusLinkOptions.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                _statusLinkOptions.Add(normalized);
                StatusLinkComboBox.ItemsSource = null;
                StatusLinkComboBox.ItemsSource = _statusLinkOptions;
            }

            var matched = _statusLinkOptions.FirstOrDefault(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(matched))
            {
                StatusLinkComboBox.SelectedItem = matched;
            }
        }

        private static string NormalizeStatusLink(string? value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            if (string.Equals(text, "open", StringComparison.OrdinalIgnoreCase))
            {
                return "Open";
            }

            if (string.Equals(text, "close", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "closed", StringComparison.OrdinalIgnoreCase))
            {
                return "Closed";
            }

            if (string.Equals(text, "cancel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return "Cancel";
            }

            return text;
        }

        private async Task RefreshSavedFormsAsync(string? preferredSelectedId = null)
        {
            try
            {
                var currentSelectedId = (SavedFormsComboBox.SelectedItem as LocalFormRecord)?.Id;
                var data = await LocalFormStorageService.GetAllAsync();
                _savedForms = data.Take(MaxSavedFormsInCombo).ToList();
                SavedFormsComboBox.ItemsSource = _savedForms;

                var targetId = !string.IsNullOrWhiteSpace(preferredSelectedId) ? preferredSelectedId : currentSelectedId;
                if (!string.IsNullOrWhiteSpace(targetId))
                {
                    var selected = _savedForms.FirstOrDefault(x => string.Equals(x.Id, targetId, StringComparison.OrdinalIgnoreCase));
                    if (selected is not null)
                    {
                        SavedFormsComboBox.SelectedItem = selected;
                    }
                }
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("CreateTtPage.RefreshSavedFormsAsync", ex);
            }
        }

        private void ApplySavedForm(LocalFormRecord record)
        {
            _isApplyingSavedForm = true;
            try
            {
                TtIohTextBox.Text = string.IsNullOrWhiteSpace(record.TtIoh)
                    ? ExtractTtIoh(record.Title)
                    : NormalizeTtIoh(record.TtIoh);
                TitleTextBox.Text = record.Title;
                OccurTimeTextBox.Text = FormatDateTime(record.OccurDateTime);
                DispatchTimeTextBox.Text = FormatDateTime(record.DispatchDateTime);
                ApplyStatusLinkSelection(record.StatusLink);
                RootCauseTextBox.Text = record.RootCause;
                CutPointTextBox.Text = record.CutPoint;
                ShowSegmentRouteToggleSwitch.IsOn = record.ShowSegmentRoute;
                ShowSystemKeyToggleSwitch.IsOn = record.ShowSystemKey;
                SystemKeyTextBox.Text = record.SystemKey;
                SegmentRouteAutoSuggestBox.Tag = record.SegmentRoute;
                SegmentRouteAutoSuggestBox.Text = record.SegmentRoute;
                PicAutoSuggestBox.Text = record.Pic;
                CoordinateTextBox.Text = record.Coordinate;
                UpdateProgressTextBox.Text = record.UpdateProgress;
                SaveNameTextBox.Text = record.Name;
                _impactListItems.Clear();
                _impactListItems.AddRange(CloneImpactList(record.ImpactList));
                RefreshImpactListUi();
            }
            finally
            {
                _isApplyingSavedForm = false;
            }

            ApplySavedRecordReferenceToActiveTab(record);
            UpdateTemplatePreview();
            UpdateActiveTabHeaderFromCurrentForm();
            MarkActiveTabSaved();
            _ = TryAutoFillSegmentRouteFromSystemKeyAsync(keepExistingPic: true, keepExistingSegment: true);
        }

        private string GetTabHeaderLabel(FormTabState state)
        {
            return state.IsDirty ? state.Header + " *" : state.Header;
        }

        private void MarkActiveTabDirty()
        {
            if (_isApplyingSavedForm || _isSwitchingTabs || string.IsNullOrWhiteSpace(_activeTabId))
            {
                return;
            }

            if (_tabStates.TryGetValue(_activeTabId, out var state) && !state.IsDirty)
            {
                state.IsDirty = true;
                _tabStates[_activeTabId] = state;
                RefreshTabOptions();
            }
        }

        private void MarkActiveTabSaved()
        {
            if (string.IsNullOrWhiteSpace(_activeTabId))
            {
                return;
            }

            if (_tabStates.TryGetValue(_activeTabId, out var state))
            {
                state.IsDirty = false;
                _tabStates[_activeTabId] = state;
                RefreshTabOptions();
            }
        }

        private async Task<bool> ConfirmTabCloseAsync(string tabId)
        {
            if (!_tabStates.TryGetValue(tabId, out var state))
            {
                return true;
            }

            if (!state.IsDirty && !HasTabContent(state))
            {
                return true;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Tutup tab?",
                Content = "Data pada tab " + state.Header + " belum disimpan. Tetap tutup tab ini?",
                PrimaryButtonText = "Tutup Tab",
                CloseButtonText = "Batal",
                DefaultButton = ContentDialogButton.Close
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        private static bool HasTabContent(FormTabState state)
        {
            return
                !string.IsNullOrWhiteSpace(state.TtIoh) ||
                !string.IsNullOrWhiteSpace(state.Title) ||
                !string.IsNullOrWhiteSpace(state.StatusLink) ||
                !string.IsNullOrWhiteSpace(state.Pic) ||
                !string.IsNullOrWhiteSpace(state.RootCause) ||
                !string.IsNullOrWhiteSpace(state.CutPoint) ||
                !string.IsNullOrWhiteSpace(state.SegmentRoute) ||
                !string.IsNullOrWhiteSpace(state.SystemKey) ||
                !string.IsNullOrWhiteSpace(state.Coordinate) ||
                !string.IsNullOrWhiteSpace(state.UpdateProgress) ||
                (state.ImpactList?.Any(item => !string.IsNullOrWhiteSpace(item.Impact)) ?? false) ||
                !state.ShowSegmentRoute ||
                !state.ShowSystemKey;
        }

        private sealed class FormTabState
        {
            public string TabId { get; set; } = string.Empty;
            public string Header { get; set; } = string.Empty;
            public bool IsHeaderManuallyEdited { get; set; }
            public bool IsDirty { get; set; }
            public string DraftName { get; set; } = string.Empty;
            public string SavedRecordId { get; set; } = string.Empty;
            public string SavedRecordName { get; set; } = string.Empty;
            public string TtIoh { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public DateTimeOffset OccurDateTime { get; set; }
            public DateTimeOffset DispatchDateTime { get; set; }
            public string StatusLink { get; set; } = string.Empty;
            public string Pic { get; set; } = string.Empty;
            public string RootCause { get; set; } = string.Empty;
            public string CutPoint { get; set; } = string.Empty;
            public bool ShowSegmentRoute { get; set; }
            public bool ShowSystemKey { get; set; }
            public string SegmentRoute { get; set; } = string.Empty;
            public string SystemKey { get; set; } = string.Empty;
            public string Coordinate { get; set; } = string.Empty;
            public string UpdateProgress { get; set; } = string.Empty;
            public List<ImpactListItem> ImpactList { get; set; } = new();
        }
    }
}
