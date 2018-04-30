﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GalaSoft.MvvmLight.Threading;
using Popcorn.ViewModels.Pages.Player;
using System.Threading;
using System.Windows.Media;
using GalaSoft.MvvmLight.Ioc;
using GalaSoft.MvvmLight.Messaging;
using NLog;
using Popcorn.Messaging;
using Popcorn.Models.Bandwidth;
using Popcorn.Services.Application;
using Popcorn.Utils;
using Popcorn.Utils.Exceptions;
using Popcorn.ViewModels.Windows.Settings;
using System.Runtime.CompilerServices;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;
using GalaSoft.MvvmLight.CommandWpf;
using Popcorn.Converters;
using Popcorn.Extensions;
using Popcorn.Helpers;
using Popcorn.Models.Chromecast;
using Unosquare.FFME;
using Unosquare.FFME.Events;

namespace Popcorn.UserControls.Player
{
    /// <summary>
    /// Interaction logic for PlayerUserControl.xaml
    /// </summary>
    public partial class PlayerUserControl : INotifyPropertyChanged
    {
        /// <summary>
        /// Logger of the class
        /// </summary>
        private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Used to update the activity mouse and mouse position.
        /// </summary>
        private DispatcherTimer ActivityTimer { get; set; }

        /// <summary>
        /// Get or set the mouse position when inactive
        /// </summary>
        private Point InactiveMousePosition { get; set; } = new Point(0, 0);

        /// <summary>
        /// Subtitle delay
        /// </summary>
        private int SubtitleDelay { get; set; }

        private ICommand _setLowerSubtitleSizeCommand;

        private ICommand _setHigherSubtitleSizeCommand;

        private ICommand _pauseCommand;

        private ICommand _playCommand;

        private double _progress;

        /// <summary>
        /// Semaphore used to update mouse activity
        /// </summary>
        private static readonly SemaphoreSlim MouseActivitySemaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Semaphore used to update subtitle delay
        /// </summary>
        private static readonly SemaphoreSlim SubtitleDelaySemaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Application service
        /// </summary>
        private readonly IApplicationService _applicationService;

        /// <summary>
        /// Initializes a new instance of the MoviePlayer class.
        /// </summary>
        public PlayerUserControl()
        {
            _applicationService = SimpleIoc.Default.GetInstance<IApplicationService>();
            InitializeComponent();
            AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDownEvent));
            Loaded += OnLoaded;
            Media.MediaOpened += OnMediaOpened;
            PauseCommand = new RelayCommand(async () => { await PauseMedia(); }, MediaPlayerPauseCanExecute);
            PlayCommand = new RelayCommand(async () => { await PlayMedia(); }, MediaPlayerPlayCanExecute);
        }

        private void OnLoaded(object sender, RoutedEventArgs routedEventArgs)
        {
            var window = System.Windows.Window.GetWindow(this);
            if (window != null)
            {
                window.Closing += async (s1, e1) => await Unload();
            }

            var vm = DataContext as MediaPlayerViewModel;
            if (vm?.MediaPath == null)
                return;

            var applicationSettings = SimpleIoc.Default.GetInstance<ApplicationSettingsViewModel>();
            Subtitles.SetFontSize(Media, applicationSettings.SelectedSubtitleSize?.Size ?? 22);
            Subtitles.SetForeground(Media, new SolidColorBrush(applicationSettings.SubtitlesColor));

            // start the activity timer used to manage visibility of the PlayerStatusBar
            ActivityTimer =
                new DispatcherTimer(DispatcherPriority.Background) {Interval = TimeSpan.FromSeconds(2)};
            ActivityTimer.Tick += OnInactivity;
            ActivityTimer.Start();

            InputManager.Current.PreProcessInput += OnActivity;

            SetLowerSubtitleSizeCommand = new RelayCommand(() =>
            {
                var currentSize = Subtitles.GetFontSize(Media);
                Subtitles.SetFontSize(Media, --currentSize);
            });

            SetHigherSubtitleSizeCommand = new RelayCommand(() =>
            {
                var currentSize = Subtitles.GetFontSize(Media);
                Subtitles.SetFontSize(Media, ++currentSize);
            });

            Subtitles.SetFontFamily(Media, new FontFamily("Verdana"));
            Subtitles.SetFontWeight(Media, FontWeights.Bold);
            Subtitles.SetOutlineWidth(Media, new Thickness(0, 0, 0, 0));
            vm.StoppedMedia += OnStoppedMedia;
            vm.PausedMedia += OnPausedMedia;
            vm.ResumedMedia += OnResumedMedia;
            vm.CastStarted += OnCastStarted;
            vm.CastStopped += OnCastStopped;
            vm.CastStatusChanged += OnCastStatusChanged;
            if (vm.BufferProgress != null)
            {
                vm.BufferProgress.ProgressChanged += OnProgressChanged;
            }

            if (vm.BandwidthRate != null)
            {
                vm.BandwidthRate.ProgressChanged += OnBandwidthChanged;
            }

            if (vm.MediaType == MediaType.Trailer)
            {
                DownloadProgress.Visibility = Visibility.Collapsed;
            }

            Title.Text = vm.MediaName;
            Media.Source = new Uri(vm.MediaPath);
            Media.MediaOpening += OnMediaOpening;
        }

        public double Progress
        {
            get => _progress;
            set
            {
                _progress = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Toggle playing media when space key is pressed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void OnPreviewKeyDownEvent(object sender,
            RoutedEventArgs e)
        {
            FocusManager.SetIsFocusScope(this, true);
            FocusManager.SetFocusedElement(this, this);

            if (e is KeyEventArgs ke)
            {
                ke.Handled = true;
                if (ke.Key == Key.Space)
                {
                    if (Media.IsPlaying)
                        await PauseMedia();
                    else
                        await PlayMedia();
                }

                if (ke.Key == Key.Up)
                {
                    Media.Volume += 0.05;
                }

                if (ke.Key == Key.Down)
                {
                    Media.Volume -= 0.05;
                }

                if (ke.Key == Key.F)
                {
                    _applicationService.IsFullScreen = !_applicationService.IsFullScreen;
                }

                if (!Media.HasSubtitles)
                    return;

                switch (ke.Key)
                {
                    case Key.H:
                        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                        {
                            SubtitleDelay += 1000;
                        }
                        else
                        {
                            SubtitleDelay += 1000;
                        }

                        break;
                    case Key.G:
                        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                        {
                            SubtitleDelay -= 1000;
                        }
                        else
                        {
                            SubtitleDelay -= 1000;
                        }

                        break;
                    default:
                        return;
                }

                Delay.Text = $"Subtitle delay: {SubtitleDelay} ms";
                if (SubtitleDelaySemaphore.CurrentCount == 0) return;
                await SubtitleDelaySemaphore.WaitAsync();
                var increasedPanelOpacityAnimation = new DoubleAnimationUsingKeyFrames
                {
                    Duration = new Duration(TimeSpan.FromSeconds(0.1)),
                    KeyFrames = new DoubleKeyFrameCollection
                    {
                        new EasingDoubleKeyFrame(0.6, KeyTime.FromPercent(1.0), new PowerEase
                        {
                            EasingMode = EasingMode.EaseInOut
                        })
                    }
                };
                var increasedSubtitleDelayOpacityAnimation = new DoubleAnimationUsingKeyFrames
                {
                    Duration = new Duration(TimeSpan.FromSeconds(0.1)),
                    KeyFrames = new DoubleKeyFrameCollection
                    {
                        new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), new PowerEase
                        {
                            EasingMode = EasingMode.EaseInOut
                        })
                    }
                };

                SubtitleDelayPanel.BeginAnimation(OpacityProperty, increasedPanelOpacityAnimation);
                Delay.BeginAnimation(OpacityProperty, increasedSubtitleDelayOpacityAnimation);
                await Task.Delay(2000);
                var decreasedOpacityAnimation = new DoubleAnimationUsingKeyFrames
                {
                    Duration = new Duration(TimeSpan.FromSeconds(0.1)),
                    KeyFrames = new DoubleKeyFrameCollection
                    {
                        new EasingDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0), new PowerEase
                        {
                            EasingMode = EasingMode.EaseInOut
                        })
                    }
                };

                SubtitleDelayPanel.BeginAnimation(OpacityProperty, decreasedOpacityAnimation);
                Delay.BeginAnimation(OpacityProperty, decreasedOpacityAnimation);
                SubtitleDelaySemaphore.Release();
            }
        }

        /// <summary>
        /// Subscribe to events and play the movie when control has been loaded
        /// </summary>
        /// <param name="sender">Sender object</param>
        /// <param name="e">EventArgs</param>
        private async void OnMediaOpened(object sender, RoutedEventArgs e)
        {
            Media.MediaEnded += MediaPlayerEndReached;
            Media.MediaFailed += EncounteredError;
            Media.SeekingStarted += OnBufferingStarted;
            Media.SeekingEnded += OnBufferingEnded;
            await PlayMedia();
        }

        private void OnMediaOpening(object sender, MediaOpeningRoutedEventArgs e)
        {
            try
            {
                if (!(DataContext is MediaPlayerViewModel vm) || string.IsNullOrEmpty(vm.SubtitleFilePath))
                    return;

                var url = new Uri(vm.SubtitleFilePath);
                if (url.IsFile || url.IsUnc)
                {
                    if (System.IO.File.Exists(vm.SubtitleFilePath))
                        e.Options.SubtitlesUrl = vm.SubtitleFilePath;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private void OnBufferingEnded(object sender, RoutedEventArgs e)
        {
            Buffering.Visibility = Visibility.Collapsed;
        }

        private void OnBufferingStarted(object sender, RoutedEventArgs e)
        {
            Buffering.Visibility = Visibility.Visible;
        }

        private void OnProgressChanged(object sender, double progress)
        {
            DispatcherHelper.CheckBeginInvokeOnUI(() =>
            {
                if (!(DataContext is MediaPlayerViewModel vm) || !Media.NaturalDuration.HasTimeSpan)
                    return;

                Progress = progress;
                vm.MediaLength = Media.NaturalDuration.TimeSpan.TotalSeconds;
                vm.PlayerTime = PositionSlider.Value;
                BufferingSlider.Value = progress / 100d;
            });
        }

        /// <summary>
        /// When cast is being played
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnCastStatusChanged(object sender, MediaStatusEventArgs e)
        {
            DispatcherHelper.CheckBeginInvokeOnUI(async () =>
            {
                if (e.Status?.PlayerState == "PLAYING")
                {
                    Media.Position = TimeSpan.FromSeconds(e.Status.CurrentTime);
                    if (!Media.IsPlaying)
                        await PlayMedia();
                }
            });
        }

        /// <summary>
        /// Unmute media when cast has stopped
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnCastStopped(object sender, EventArgs e)
        {
            Media.IsMuted = false;
        }

        /// <summary>
        /// Mute media when cast has started
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnCastStarted(object sender, EventArgs e)
        {
            Media.IsMuted = true;
        }

        /// <summary>
        /// On player resumed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnResumedMedia(object sender, EventArgs e)
        {
            DispatcherHelper.CheckBeginInvokeOnUI(async () => await PlayMedia());
        }

        /// <summary>
        /// On pause player
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnPausedMedia(object sender, EventArgs e)
        {
            DispatcherHelper.CheckBeginInvokeOnUI(async () => await PauseMedia());
        }

        /// <summary>
        /// When bandwidth rate has changed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnBandwidthChanged(object sender, BandwidthRate e)
        {
            DispatcherHelper.CheckBeginInvokeOnUI(() =>
            {
                Download.Text = e.DownloadRate.ToString(CultureInfo.InvariantCulture);
                Upload.Text = e.UploadRate.ToString(CultureInfo.InvariantCulture);
                Remaining.Text = new TimeSpanFormatter().Convert(e.ETA, null, null, null).ToString();
            });
        }

        /// <summary>
        /// Vlc encounters an error. Warn the user of this
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void EncounteredError(object sender, EventArgs e)
        {
            DispatcherHelper.CheckBeginInvokeOnUI(() =>
            {
                Messenger.Default.Send(
                    new UnhandledExceptionMessage(
                        new PopcornException("An error has occured while trying to play the media.")));
                if (!(DataContext is MediaPlayerViewModel vm))
                    return;

                vm.MediaEnded();
            });
        }

        /// <summary>
        /// When user uses the mousewheel, update the volume
        /// </summary>
        /// <param name="sender">Sender object</param>
        /// <param name="e">MouseWheelEventArgs</param>
        private void MouseWheelMediaPlayer(object sender, MouseWheelEventArgs e)
        {
            Media.Volume += e.Delta > 0 ? 0.05 : -0.05;
        }

        /// <summary>
        /// When a movie has been seen, save this information in the user data
        /// </summary>
        /// <param name="sender">Sender object</param>
        /// <param name="e">EventArgs</param>
        private void MediaPlayerEndReached(object sender, EventArgs e)
        {
            DispatcherHelper.CheckBeginInvokeOnUI(() =>
            {
                if (!(DataContext is MediaPlayerViewModel vm))
                    return;

                vm.MediaEnded();
            });
        }

        /// <summary>
        /// Play the movie
        /// </summary>
        private async Task PlayMedia()
        {
            try
            {
                _applicationService.SwitchConstantDisplayAndPower(true);
                MediaPlayerStatusBarItemPlay.Visibility = Visibility.Collapsed;
                MediaPlayerStatusBarItemPause.Visibility = Visibility.Visible;
                CastButton.Visibility = Visibility.Visible;
                if (DataContext is MediaPlayerViewModel vm && Media.NaturalDuration.HasTimeSpan)
                {
                    vm.MediaDuration = Media.NaturalDuration.TimeSpan.TotalSeconds;
                    if (vm.IsCasting)
                        vm.PlayCastCommand.Execute(null);
                }

                await Media.Play();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                Messenger.Default.Send(
                    new UnhandledExceptionMessage(
                        new PopcornException("An error has occured while trying to play the media.")));
                if (!(DataContext is MediaPlayerViewModel vm))
                    return;

                vm.MediaEnded();
            }
        }

        /// <summary>
        /// Pause the movie
        /// </summary>
        private async Task PauseMedia()
        {
            try
            {
                if (DataContext is MediaPlayerViewModel vm && vm.IsCasting)
                {
                    vm.PauseCastCommand.Execute(null);
                }

                _applicationService.SwitchConstantDisplayAndPower(false);
                MediaPlayerStatusBarItemPlay.Visibility = Visibility.Visible;
                MediaPlayerStatusBarItemPause.Visibility = Visibility.Collapsed;
                await Media.Pause();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        /// <summary>
        /// When media has finished playing, dispose the control
        /// </summary>
        /// <param name="sender">Sender object</param>
        /// <param name="e">EventArgs</param>
        private async void OnStoppedMedia(object sender, EventArgs e) => await Unload();

        /// <summary>
        /// Each time the CanExecute play command change, update the visibility of Play/Pause buttons in the player
        /// </summary>
        private bool MediaPlayerPlayCanExecute()
        {
            if (Media.IsPlaying)
            {
                MediaPlayerStatusBarItemPlay.Visibility = Visibility.Collapsed;
                MediaPlayerStatusBarItemPause.Visibility = Visibility.Visible;
            }
            else
            {
                MediaPlayerStatusBarItemPlay.Visibility = Visibility.Visible;
                MediaPlayerStatusBarItemPause.Visibility = Visibility.Collapsed;
            }

            return !Media.IsPlaying;
        }

        /// <summary>
        /// Each time the CanExecute play command change, update the visibility of Play/Pause buttons in the media player
        /// </summary>
        private bool MediaPlayerPauseCanExecute()
        {
            if (Media.IsPlaying)
            {
                MediaPlayerStatusBarItemPlay.Visibility = Visibility.Collapsed;
                MediaPlayerStatusBarItemPause.Visibility = Visibility.Visible;
            }
            else
            {
                MediaPlayerStatusBarItemPlay.Visibility = Visibility.Visible;
                MediaPlayerStatusBarItemPause.Visibility = Visibility.Collapsed;
            }

            return Media.CanPause;
        }

        /// <summary>
        /// Hide the PlayerStatusBar on mouse inactivity
        /// </summary>
        /// <param name="sender">Sender object</param>
        /// <param name="e">EventArgs</param>
        private void OnInactivity(object sender, EventArgs e)
        {
            if (InactiveMousePosition == Mouse.GetPosition(Container))
            {
                var window = System.Windows.Window.GetWindow(this);
                if (window != null)
                {
                    window.Cursor = Cursors.None;
                }

                var opacityAnimation = new DoubleAnimationUsingKeyFrames
                {
                    Duration = new Duration(TimeSpan.FromSeconds(0.5)),
                    KeyFrames = new DoubleKeyFrameCollection
                    {
                        new EasingDoubleKeyFrame(0.0, KeyTime.FromPercent(1d), new PowerEase
                        {
                            EasingMode = EasingMode.EaseInOut
                        })
                    }
                };

                PlayerStatusBar.BeginAnimation(OpacityProperty, opacityAnimation);
                UpperPanel.BeginAnimation(OpacityProperty, opacityAnimation);
            }

            InactiveMousePosition = Mouse.GetPosition(Container);
        }

        /// <summary>
        /// Show the PlayerStatusBar on mouse activity
        /// </summary>
        /// <param name="sender">Sender object</param>
        /// <param name="e">EventArgs</param>
        private async void OnActivity(object sender, PreProcessInputEventArgs e)
        {
            await MouseActivitySemaphore.WaitAsync();
            if (e.StagingItem == null)
            {
                MouseActivitySemaphore.Release();
                return;
            }

            var inputEventArgs = e.StagingItem.Input;
            if (!(inputEventArgs is MouseEventArgs) && !(inputEventArgs is KeyboardEventArgs))
            {
                MouseActivitySemaphore.Release();
                return;
            }

            var mouseEventArgs = e.StagingItem.Input as MouseEventArgs;

            // no button is pressed and the position is still the same as the application became inactive
            if (mouseEventArgs?.LeftButton == MouseButtonState.Released &&
                mouseEventArgs.RightButton == MouseButtonState.Released &&
                mouseEventArgs.MiddleButton == MouseButtonState.Released &&
                mouseEventArgs.XButton1 == MouseButtonState.Released &&
                mouseEventArgs.XButton2 == MouseButtonState.Released &&
                InactiveMousePosition == mouseEventArgs.GetPosition(Container))
            {
                MouseActivitySemaphore.Release();
                return;
            }

            var opacityAnimation = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(TimeSpan.FromSeconds(0.1)),
                KeyFrames = new DoubleKeyFrameCollection
                {
                    new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), new PowerEase
                    {
                        EasingMode = EasingMode.EaseInOut
                    })
                }
            };

            PlayerStatusBar.BeginAnimation(OpacityProperty, opacityAnimation);
            UpperPanel.BeginAnimation(OpacityProperty, opacityAnimation);
            var window = System.Windows.Window.GetWindow(this);
            if (window != null)
            {
                window.Cursor = Cursors.Arrow;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
            MouseActivitySemaphore.Release();
        }

        /// <summary>
        /// Command used to lower subtitle size
        /// </summary>
        public ICommand SetLowerSubtitleSizeCommand
        {
            get => _setLowerSubtitleSizeCommand;
            set
            {
                _setLowerSubtitleSizeCommand = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Command used to lower subtitle size
        /// </summary>
        public ICommand SetHigherSubtitleSizeCommand
        {
            get => _setHigherSubtitleSizeCommand;
            set
            {
                _setHigherSubtitleSizeCommand = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Command used to pause media
        /// </summary>
        public ICommand PauseCommand
        {
            get => _pauseCommand;
            set
            {
                _pauseCommand = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Command used to play media
        /// </summary>
        public ICommand PlayCommand
        {
            get => _playCommand;
            set
            {
                _playCommand = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Dispose the control
        /// </summary>
        private async Task Unload()
        {
            try
            {
                Loaded -= OnMediaOpened;
                ActivityTimer.Tick -= OnInactivity;
                ActivityTimer.Stop();

                InputManager.Current.PreProcessInput -= OnActivity;

                Media.MediaFailed -= EncounteredError;
                Media.MediaEnded -= MediaPlayerEndReached;
                var window = System.Windows.Window.GetWindow(this);
                if (window != null)
                {
                    window.Cursor = Cursors.Arrow;
                }

                var vm = DataContext as MediaPlayerViewModel;
                if (vm != null)
                {
                    vm.StoppedMedia -= OnStoppedMedia;
                    vm.ResumedMedia -= OnResumedMedia;
                    vm.PausedMedia -= OnPausedMedia;
                }

                if (vm?.BandwidthRate != null)
                {
                    vm.BandwidthRate.ProgressChanged -= OnBandwidthChanged;
                }

                _applicationService.SwitchConstantDisplayAndPower(false);
                RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDownEvent));
                await Media.Close();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private async Task SeekMedia()
        {
            Media.Position = TimeSpan.FromSeconds(PositionSlider.Value);
            if (DataContext is MediaPlayerViewModel vm && vm.IsCasting)
            {
                vm.SeekCastCommand.Execute(Media.Position.TotalSeconds);
            }

            await PlayMedia();
        }

        private async void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (Media.IsPlaying)
                await PauseMedia();
            else
                await PlayMedia();
        }
    }
}