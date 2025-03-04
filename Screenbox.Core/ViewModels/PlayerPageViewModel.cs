﻿#nullable enable

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Microsoft.Toolkit.Uwp.UI;
using Screenbox.Core.Enums;
using Screenbox.Core.Events;
using Screenbox.Core.Helpers;
using Screenbox.Core.Messages;
using Screenbox.Core.Models;
using Screenbox.Core.Playback;
using Screenbox.Core.Services;
using System;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Screenbox.Core.ViewModels
{
    public sealed partial class PlayerPageViewModel : ObservableRecipient,
        IRecipient<UpdateStatusMessage>,
        IRecipient<UpdateVolumeStatusMessage>,
        IRecipient<TogglePlayerVisibilityMessage>,
        IRecipient<SuspendingMessage>,
        IRecipient<MediaPlayerChangedMessage>,
        IRecipient<PlaylistActiveItemChangedMessage>,
        IRecipient<ShowPlayPauseBadgeMessage>,
        IRecipient<OverrideControlsHideDelayMessage>,
        IRecipient<PlayerVisibilityRequestMessage>,
        IRecipient<PropertyChangedMessage<NavigationViewDisplayMode>>
    {
        [ObservableProperty] private bool? _audioOnly;
        [ObservableProperty] private bool _controlsHidden;
        [ObservableProperty] private string? _statusMessage;
        [ObservableProperty] private bool _isPlaying;
        [ObservableProperty] private bool _isPlayingBadge;
        [ObservableProperty] private bool _isOpening;
        [ObservableProperty] private bool _showPlayPauseBadge;
        [ObservableProperty] private WindowViewMode _viewMode;
        [ObservableProperty] private NavigationViewDisplayMode _navigationViewDisplayMode;
        [ObservableProperty] private MediaViewModel? _media;

        [ObservableProperty]
        [NotifyPropertyChangedRecipients]
        private PlayerVisibilityState _playerVisibility;

        [ObservableProperty]
        [NotifyPropertyChangedRecipients]
        private MediaPlaybackState _playbackState;

        public bool SeekBarPointerInteracting { get; set; }

        private bool AudioOnlyInternal => AudioOnly ?? false;

        private readonly DispatcherQueue _dispatcherQueue;
        private readonly DispatcherQueueTimer _openingTimer;
        private readonly DispatcherQueueTimer _controlsVisibilityTimer;
        private readonly DispatcherQueueTimer _statusMessageTimer;
        private readonly DispatcherQueueTimer _playPauseBadgeTimer;
        private readonly IWindowService _windowService;
        private readonly ISettingsService _settingsService;
        private readonly IResourceService _resourceService;
        private readonly LastPositionTracker _lastPositionTracker;
        private IMediaPlayer? _mediaPlayer;
        private bool _visibilityOverride;
        private DateTimeOffset _lastUpdated;

        public PlayerPageViewModel(IWindowService windowService, IResourceService resourceService, ISettingsService settingsService)
        {
            _windowService = windowService;
            _resourceService = resourceService;
            _settingsService = settingsService;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _openingTimer = _dispatcherQueue.CreateTimer();
            _controlsVisibilityTimer = _dispatcherQueue.CreateTimer();
            _statusMessageTimer = _dispatcherQueue.CreateTimer();
            _playPauseBadgeTimer = _dispatcherQueue.CreateTimer();
            _navigationViewDisplayMode = Messenger.Send<NavigationViewDisplayModeRequestMessage>();
            _playerVisibility = PlayerVisibilityState.Hidden;
            _lastPositionTracker = new LastPositionTracker();
            _lastUpdated = DateTimeOffset.MinValue;

            FocusManager.GotFocus += FocusManagerOnFocusChanged;
            _windowService.ViewModeChanged += WindowServiceOnViewModeChanged;

            // Activate the view model's messenger
            IsActive = true;
        }

        public void Receive(PlayerVisibilityRequestMessage message)
        {
            message.Reply(PlayerVisibility);
        }

        public void Receive(TogglePlayerVisibilityMessage message)
        {
            switch (PlayerVisibility)
            {
                case PlayerVisibilityState.Visible:
                    GoBack();
                    break;
                case PlayerVisibilityState.Minimal:
                    RestorePlayer();
                    break;
            }
        }

        public void Receive(PropertyChangedMessage<NavigationViewDisplayMode> message)
        {
            NavigationViewDisplayMode = message.NewValue;
        }

        private void WindowServiceOnViewModeChanged(object sender, ViewModeChangedEventArgs e)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                ViewMode = e.NewValue;
            });
        }

        public void Receive(SuspendingMessage message)
        {
            message.Reply(_lastPositionTracker.SaveToDiskAsync());
        }

        public async void Receive(MediaPlayerChangedMessage message)
        {
            _mediaPlayer = message.Value;
            _mediaPlayer.PlaybackStateChanged += OnStateChanged;
            _mediaPlayer.PositionChanged += OnPositionChanged;

            await _lastPositionTracker.LoadFromDiskAsync();
        }

        public void Receive(UpdateVolumeStatusMessage message)
        {
            Receive(new UpdateStatusMessage(
                _resourceService.GetString(ResourceName.VolumeChangeStatusMessage, message.Value), message.Persistent));
        }

        public void Receive(UpdateStatusMessage message)
        {
            // Don't show status message when player is not visible
            if (PlayerVisibility != PlayerVisibilityState.Visible && !string.IsNullOrEmpty(message.Value)) return;

            _dispatcherQueue.TryEnqueue(() =>
            {
                StatusMessage = message.Value;
                if (message.Persistent || message.Value == null) return;
                _statusMessageTimer.Debounce(() => StatusMessage = null, TimeSpan.FromSeconds(1));
            });
        }

        public void Receive(PlaylistActiveItemChangedMessage message)
        {
            _dispatcherQueue.TryEnqueue(() => ProcessOpeningMedia(message.Value));
            if (message.Value != null)
            {
                TimeSpan lastPosition = _lastPositionTracker.GetPosition(message.Value.Location);
                Messenger.Send(new RaiseResumePositionNotificationMessage(lastPosition));
            }
        }

        public void Receive(ShowPlayPauseBadgeMessage message)
        {
            IsPlayingBadge = message.IsPlaying;
            BlinkPlayPauseBadge();
        }

        public void Receive(OverrideControlsHideDelayMessage message)
        {
            OverrideControlsDelayHide(message.Delay);
        }

        public bool OnPlayerClick()
        {
            if (!ControlsHidden) return !_settingsService.PlayerTapGesture && TryHideControls(true);
            ControlsHidden = false;
            DelayHideControls();
            return true;
        }

        public void OnPointerMoved()
        {
            if (_visibilityOverride) return;
            ControlsHidden = false;

            if (SeekBarPointerInteracting) return;
            DelayHideControls();
        }

        public void OnVolumeKeyboardAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (_mediaPlayer == null || sender.Modifiers != VirtualKeyModifiers.None) return;
            bool playerVisible = PlayerVisibility == PlayerVisibilityState.Visible;
            int volumeChange;
            VirtualKey key = sender.Key;

            switch (key)
            {
                case (VirtualKey)0xBB:  // Plus ("+")
                case (VirtualKey)0x6B:  // Add ("+")(Numpad plus)
                case VirtualKey.Up when playerVisible:
                    volumeChange = 5;
                    break;
                case (VirtualKey)0xBD:  // Minus ("-")
                case (VirtualKey)0x6D:  // Subtract ("-")(Numpad minus)
                case VirtualKey.Down when playerVisible:
                    volumeChange = -5;
                    break;
                default:
                    return;
            }

            int volume = Messenger.Send(new ChangeVolumeRequestMessage(volumeChange, true));
            Messenger.Send(new UpdateVolumeStatusMessage(volume, false));
            args.Handled = true;
        }

        public void OnSeekKeyboardAcceleratorInvoked(KeyboardAccelerator sender,
            KeyboardAcceleratorInvokedEventArgs args)
        {
            if (_mediaPlayer == null) return;
            bool playerVisible = PlayerVisibility == PlayerVisibilityState.Visible;
            long seekAmount = 0;
            int direction;
            switch (sender.Key)
            {
                case VirtualKey.Left when playerVisible:
                case VirtualKey.J:
                    direction = -1;
                    break;
                case VirtualKey.Right when playerVisible:
                case VirtualKey.L:
                    direction = 1;
                    break;
                default:
                    return;
            }

            switch (sender.Modifiers)
            {
                case VirtualKeyModifiers.Control:
                    seekAmount = 10000;
                    break;
                case VirtualKeyModifiers.Shift:
                    seekAmount = 1000;
                    break;
                case VirtualKeyModifiers.None:
                    seekAmount = 5000;
                    break;
            }

            seekAmount *= direction;
            if (seekAmount != 0)
            {
                Messenger.SendSeekWithStatus(TimeSpan.FromMilliseconds(seekAmount));
            }

            args.Handled = true;
        }

        public void OnPercentJumpKeyboardAcceleratorInvoked(KeyboardAccelerator sender,
            KeyboardAcceleratorInvokedEventArgs args)
        {
            if (_mediaPlayer == null || PlayerVisibility != PlayerVisibilityState.Visible) return;
            VirtualKey key = sender.Key;
            switch (key)
            {
                case VirtualKey.NumberPad0:
                case VirtualKey.NumberPad1:
                case VirtualKey.NumberPad2:
                case VirtualKey.NumberPad3:
                case VirtualKey.NumberPad4:
                case VirtualKey.NumberPad5:
                case VirtualKey.NumberPad6:
                case VirtualKey.NumberPad7:
                case VirtualKey.NumberPad8:
                case VirtualKey.NumberPad9:
                    int percent = (key - VirtualKey.NumberPad0) * 10;
                    TimeSpan newPosition = _mediaPlayer.NaturalDuration * (0.01 * percent);
                    PositionChangedResult result = Messenger.Send(new ChangeTimeRequestMessage(newPosition));
                    newPosition = result.NewPosition;
                    Messenger.SendPositionStatus(newPosition, result.NaturalDuration, $"{percent}%");
                    break;
                default:
                    return;
            }

            args.Handled = true;
        }

        public void OnPlaybackRateKeyboardAcceleratorInvoked(KeyboardAccelerator sender,
            KeyboardAcceleratorInvokedEventArgs args)
        {
            if (_mediaPlayer == null || sender.Modifiers != VirtualKeyModifiers.Shift ||
                PlayerVisibility != PlayerVisibilityState.Visible) return;
            args.Handled = true;
            switch (sender.Key)
            {
                case (VirtualKey)190:   // Shift + . (">")
                    TogglePlaybackRate(true);
                    return;
                case (VirtualKey)188:   // Shift + , ("<")
                    TogglePlaybackRate(false);
                    return;
            }
        }

        public void OnFrameJumpKeyboardAcceleratorInvoked(KeyboardAccelerator sender,
            KeyboardAcceleratorInvokedEventArgs args)
        {
            if (PlayerVisibility != PlayerVisibilityState.Visible || (!(_mediaPlayer?.CanSeek ?? false)) ||
                _mediaPlayer.PlaybackState != MediaPlaybackState.Paused) return;
            args.Handled = true;
            switch (sender.Key)
            {
                case (VirtualKey)190:   // Period (".")
                    _mediaPlayer.StepForwardOneFrame();
                    return;
                case (VirtualKey)188:   // Comma (",")
                    _mediaPlayer.StepBackwardOneFrame();
                    return;
            }
        }

        public void OnManualHideControlsKeyboardAcceleratorInvoked(KeyboardAccelerator sender,
            KeyboardAcceleratorInvokedEventArgs args)
        {
            if (_windowService.ViewMode != WindowViewMode.Default) return;
            if (TryHideControls())
            {
                args.Handled = true;
            }
        }

        public void RevealControls()
        {
            ControlsHidden = false;
        }

        private void TogglePlaybackRate(bool speedUp)
        {
            if (_mediaPlayer == null) return;
            Span<double> steps = stackalloc[] { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2 };
            double lastPositiveStep = steps[0];
            foreach (double step in steps)
            {
                double diff = step - _mediaPlayer.PlaybackRate;
                if (speedUp && diff > 0)
                {
                    _mediaPlayer.PlaybackRate = step;
                    Messenger.Send(new UpdateStatusMessage($"{step}×"));
                    return;
                }

                if (!speedUp)
                {
                    if (-diff > 0)
                    {
                        lastPositiveStep = step;
                    }
                    else
                    {
                        _mediaPlayer.PlaybackRate = lastPositiveStep;
                        Messenger.Send(new UpdateStatusMessage($"{lastPositiveStep}×"));
                        return;
                    }
                }
            }
        }

        partial void OnControlsHiddenChanged(bool value)
        {
            if (value)
            {
                _windowService.HideCursor();
            }
            else
            {
                _windowService.ShowCursor();
            }

            Messenger.Send(new PlayerControlsVisibilityChangedMessage(!value));
        }

        partial void OnPlayerVisibilityChanged(PlayerVisibilityState value)
        {
            if (value != PlayerVisibilityState.Visible) ControlsHidden = false;
            Messenger.Send(new PlayerVisibilityChangedMessage(value));
        }

        [RelayCommand]
        public void GoBack()
        {
            // Only allow back when not in fullscreen or compact overlay
            // Doing so would break layout logic
            switch (_windowService.ViewMode)
            {
                case WindowViewMode.FullScreen:
                    _windowService.ExitFullScreen();
                    break;
                case WindowViewMode.Compact:
                    _windowService.TryExitCompactLayoutAsync();
                    break;
                case WindowViewMode.Default:
                    PlaylistInfo playlist = Messenger.Send(new PlaylistRequestMessage());
                    bool hasItemsInQueue = playlist.Playlist.Count > 0;
                    PlayerVisibility = hasItemsInQueue ? PlayerVisibilityState.Minimal : PlayerVisibilityState.Hidden;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        [RelayCommand]
        private void RestorePlayer()
        {
            PlayerVisibility = PlayerVisibilityState.Visible;
        }

        private void BlinkPlayPauseBadge()
        {
            ShowPlayPauseBadge = true;
            _playPauseBadgeTimer.Debounce(() => ShowPlayPauseBadge = false, TimeSpan.FromMilliseconds(100));
        }

        public bool TryHideControls(bool skipFocusCheck = false)
        {
            if (PlayerVisibility != PlayerVisibilityState.Visible || !IsPlaying ||
                SeekBarPointerInteracting || AudioOnlyInternal || ControlsHidden) return false;

            if (!skipFocusCheck)
            {
                Control? focused = FocusManager.GetFocusedElement() as Control;
                // Don't hide controls when a Slider is in focus since user can interact with Slider
                // using arrow keys without affecting focus.
                if (focused is Slider { IsFocusEngaged: true }) return false;

                // Don't hide controls when a flyout is in focus
                // Flyout is not in the same XAML tree of the Window content, use this fact to detect flyout opened
                Control? root = focused?.FindAscendant<Control>(control => control == Window.Current.Content);
                if (root == null) return false;
            }

            ControlsHidden = true;

            // Workaround for PointerMoved is raised when show/hide cursor
            OverrideControlsDelayHide();

            return true;
        }

        private void DelayHideControls(int delayInSeconds = 3)
        {
            if (PlayerVisibility != PlayerVisibilityState.Visible || AudioOnlyInternal) return;
            _controlsVisibilityTimer.Debounce(() => TryHideControls(), TimeSpan.FromSeconds(delayInSeconds));
        }

        private void OverrideControlsDelayHide(int delay = 400)
        {
            _visibilityOverride = true;
            Task.Delay(delay).ContinueWith(_ => _visibilityOverride = false);
        }

        private void FocusManagerOnFocusChanged(object sender, FocusManagerGotFocusEventArgs e)
        {
            if (_visibilityOverride) return;
            ControlsHidden = false;
            DelayHideControls(4);
        }

        private async void ProcessOpeningMedia(MediaViewModel? current)
        {
            Media = current;
            if (current != null)
            {
                await current.LoadDetailsAsync();
                await current.LoadThumbnailAsync();
                AudioOnly = current.MediaType == MediaPlaybackType.Music;
                if (PlayerVisibility != PlayerVisibilityState.Visible)
                {
                    PlayerVisibility = AudioOnlyInternal ? PlayerVisibilityState.Minimal : PlayerVisibilityState.Visible;
                }
            }
            else if (PlayerVisibility == PlayerVisibilityState.Minimal)
            {
                PlayerVisibility = PlayerVisibilityState.Hidden;
            }
        }

        private void OnStateChanged(IMediaPlayer sender, object? args)
        {
            _openingTimer.Stop();
            MediaPlaybackState state = sender.PlaybackState;
            if (state == MediaPlaybackState.Opening)
            {
                _openingTimer.Debounce(() => IsOpening = state == MediaPlaybackState.Opening, TimeSpan.FromSeconds(0.5));
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                PlaybackState = state;
                IsPlaying = state == MediaPlaybackState.Playing;
                IsOpening = false;

                if (!IsPlaying)
                {
                    ControlsHidden = false;
                }

                if (!ControlsHidden && IsPlaying)
                {
                    DelayHideControls();
                }
            });
        }

        private void OnPositionChanged(IMediaPlayer sender, object? args)
        {
            // Only record position for media over 1 minute
            // Update every 3 seconds
            TimeSpan position = sender.Position;
            if (Media == null || sender.NaturalDuration <= TimeSpan.FromMinutes(1) ||
                DateTimeOffset.Now - _lastUpdated <= TimeSpan.FromSeconds(3))
                return;

            if (position > TimeSpan.FromSeconds(30) && position + TimeSpan.FromSeconds(10) < sender.NaturalDuration)
            {
                _lastUpdated = DateTimeOffset.Now;
                _lastPositionTracker.UpdateLastPosition(Media.Location, position);
            }
            else if (position > TimeSpan.FromSeconds(5))
            {
                _lastUpdated = DateTimeOffset.Now;
                _lastPositionTracker.RemovePosition(Media.Location);
            }
        }
    }
}