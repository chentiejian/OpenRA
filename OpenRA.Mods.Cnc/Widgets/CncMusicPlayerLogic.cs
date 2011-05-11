#region Copyright & License Information
/*
 * Copyright 2007-2011 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made 
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Drawing;
using System.Linq;
using OpenRA.FileFormats;
using OpenRA.Support;
using OpenRA.Widgets;
using OpenRA.Traits;
using OpenRA.Graphics;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace OpenRA.Mods.Cnc.Widgets
{
	public class CncMusicPlayerLogic : IWidgetDelegate
	{
		bool installed;
		string currentSong = null;
		Widget panel;
		string[] music;
		string[] random;

		[ObjectCreator.UseCtor]
		public CncMusicPlayerLogic([ObjectCreator.Param] Widget widget,
		                           [ObjectCreator.Param] Action onExit)
		{
			panel = widget.GetWidget("MUSIC_PANEL");
			BuildMusicTable(panel);

			currentSong = GetNextSong();
			installed = Rules.Music.Where(m => m.Value.Exists).Any();
			Func<bool> noMusic = () => !installed;
			
			panel.GetWidget<CncMenuButtonWidget>("BACK_BUTTON").OnClick = onExit;
			
			Action<string> afterInstall = path =>
			{
				// Mount the new mixfile and rebuild the scores list
				try
				{
					FileSystem.Mount(path);
					Rules.Music.Do(m => m.Value.Reload());
				}
				catch (Exception) { }
				
				installed = Rules.Music.Where(m => m.Value.Exists).Any();
				BuildMusicTable(panel);
			};
			
			var installButton = panel.GetWidget<CncMenuButtonWidget>("INSTALL_BUTTON");
			installButton.OnClick = () =>
				Widget.OpenWindow("INSTALL_MUSIC_PANEL", new Dictionary<string, object>() {{ "afterInstall", afterInstall }});
			installButton.IsVisible = () => music.Length < 2; // Hack around ra shipping (only) hellmarch by default
			
			panel.GetWidget("NO_MUSIC_LABEL").IsVisible = noMusic;

			var playButton = panel.GetWidget<CncMenuButtonWidget>("BUTTON_PLAY");
			playButton.OnClick = Play;
			playButton.IsDisabled = noMusic;
			
			var pauseButton = panel.GetWidget<CncMenuButtonWidget>("BUTTON_PAUSE");
			pauseButton.OnClick = Pause;
			pauseButton.IsDisabled = noMusic;

			var stopButton = panel.GetWidget<CncMenuButtonWidget>("BUTTON_STOP");
			stopButton.OnClick = Stop;
			stopButton.IsDisabled = noMusic;
			
			var nextButton = panel.GetWidget<CncMenuButtonWidget>("BUTTON_NEXT");
			nextButton.OnClick = () => { currentSong = GetNextSong(); Play(); };
			nextButton.IsDisabled = noMusic;
			
			var prevButton = panel.GetWidget<CncMenuButtonWidget>("BUTTON_PREV");
			prevButton.OnClick = () => { currentSong = GetPrevSong(); Play(); };
			prevButton.IsDisabled = noMusic;
			
			var shuffleCheckbox = panel.GetWidget<CncCheckboxWidget>("SHUFFLE");
			shuffleCheckbox.IsChecked = () => Game.Settings.Sound.Shuffle;
			shuffleCheckbox.OnClick = () => Game.Settings.Sound.Shuffle ^= true;
			
			var repeatCheckbox = panel.GetWidget<CncCheckboxWidget>("REPEAT");
			repeatCheckbox.IsChecked = () => Game.Settings.Sound.Repeat;
			repeatCheckbox.OnClick = () => Game.Settings.Sound.Repeat ^= true;

			panel.GetWidget<LabelWidget>("TIME_LABEL").GetText = () => (currentSong == null) ? "" : 
					"{0:D2}:{1:D2} / {2:D2}:{3:D2}".F((int)Sound.MusicSeekPosition / 60, (int)Sound.MusicSeekPosition % 60,
					    							  Rules.Music[currentSong].Length / 60, Rules.Music[currentSong].Length % 60);
		}
	
		void BuildMusicTable(Widget panel)
		{
			music = Rules.Music.Where(a => a.Value.Exists)
				.Select(a => a.Key).ToArray();
			random = music.Shuffle(Game.CosmeticRandom).ToArray();
			
			var ml = panel.GetWidget<ScrollPanelWidget>("MUSIC_LIST");
			var itemTemplate = ml.GetWidget<ContainerWidget>("MUSIC_TEMPLATE");
			
			foreach (var s in music)
			{
				var song = s;
				if (currentSong == null)
					currentSong = song;

				var template = itemTemplate.Clone() as ContainerWidget;
				template.GetBackground = () => (template.RenderBounds.Contains(Viewport.LastMousePos) ? "button-hover" : (song == currentSong) ? "button-pressed" : null);
				template.OnMouseDown = mi =>
				{
					if (mi.Button != MouseButton.Left) return false;
					currentSong = song;
					Play();
					return true;
				};

				template.IsVisible = () => true;				
				template.GetWidget<LabelWidget>("TITLE").GetText = () => Rules.Music[song].Title;
				template.GetWidget<LabelWidget>("LENGTH").GetText = () => SongLengthLabel(song);
				ml.AddChild(template);
			}
		}
		
		
		void Play()
		{
			if (currentSong == null)
				return;
			
			Sound.PlayMusicThen(Rules.Music[currentSong].Filename, () =>
			{
				if (!Game.Settings.Sound.Repeat)
					currentSong = GetNextSong();
				Play();
			});
			
			panel.GetWidget("BUTTON_PLAY").Visible = false;
			panel.GetWidget("BUTTON_PAUSE").Visible = true;
		}
		
		void Pause()
		{
			Sound.PauseMusic();
			panel.GetWidget("BUTTON_PAUSE").Visible = false;
			panel.GetWidget("BUTTON_PLAY").Visible = true;
		}
		
		void Stop()
		{
			Sound.StopMusic();
			panel.GetWidget("BUTTON_PAUSE").Visible = false;
			panel.GetWidget("BUTTON_PLAY").Visible = true;		
		}
		
		string SongLengthLabel(string song)
		{
			return "{0:D1}:{1:D2}".F(Rules.Music[song].Length / 60,
			                         Rules.Music[song].Length % 60);
		}
		
		string GetNextSong()
		{
			if (!music.Any())
				return null;
			
			var songs = Game.Settings.Sound.Shuffle ? random : music;
			return songs.SkipWhile(m => m != currentSong)
				.Skip(1).FirstOrDefault() ?? songs.FirstOrDefault();
		}

		string GetPrevSong()
		{
			if (!music.Any())
				return null;
			
			var songs = Game.Settings.Sound.Shuffle ? random : music;
			return songs.Reverse().SkipWhile(m => m != currentSong)
				.Skip(1).FirstOrDefault() ?? songs.FirstOrDefault();
		}
	}
	
	
	public class CncInstallMusicLogic : IWidgetDelegate
	{
		Widget panel;
		ProgressBarWidget progressBar;
		LabelWidget statusLabel;
		Action<string> afterInstall;
		
		[ObjectCreator.UseCtor]
		public CncInstallMusicLogic([ObjectCreator.Param] Widget widget,
		                       [ObjectCreator.Param] Action<string> afterInstall)
		{
			this.afterInstall = afterInstall;
			panel = widget.GetWidget("INSTALL_MUSIC_PANEL");
			progressBar = panel.GetWidget<ProgressBarWidget>("PROGRESS_BAR");
			statusLabel = panel.GetWidget<LabelWidget>("STATUS_LABEL");
			
			var backButton = panel.GetWidget<CncMenuButtonWidget>("BACK_BUTTON");
			backButton.OnClick = Widget.CloseWindow;
			backButton.IsVisible = () => false;
			
			var retryButton = panel.GetWidget<CncMenuButtonWidget>("RETRY_BUTTON");
			retryButton.OnClick = PromptForCD;
			retryButton.IsVisible = () => false;
			
			// TODO: Search obvious places (platform dependent) for CD
			PromptForCD();
		}
		
		void PromptForCD()
		{
			progressBar.SetIndeterminate(true);
			statusLabel.GetText = () => "Waiting for file";
			Game.Utilities.PromptFilepathAsync("Select SCORES.MIX on the C&C CD", path => Game.RunAfterTick(() => Install(path)));
		}
		
		void Install(string path)
		{
			var dest = new string[] { Platform.SupportDir, "Content", "cnc" }.Aggregate(Path.Combine);
			
			var onError = (Action<string>)(s =>
			{
				progressBar.SetIndeterminate(false);
				statusLabel.GetText = () => "Error: "+s;
				panel.GetWidget("RETRY_BUTTON").IsVisible = () => true;
				panel.GetWidget("BACK_BUTTON").IsVisible = () => true;
			});
			
			// Mount the package and check that it contains the correct files
			try
			{
				var mixFile = new MixFile(path, 0);
				
				if (!mixFile.Exists("aoi.aud"))
				{
					onError("Not the C&C SCORES.MIX");
					return;
				}
				
				statusLabel.GetText = () => "Installing";
				var t = new Thread( _ =>
				{
					var destPath = Path.Combine(dest, "scores.mix");
					try
					{
						File.Copy(path, destPath, true);
					}
					catch (Exception)
					{
						onError("File copy failed");
					}
					
					Game.RunAfterTick(() =>
					{
						Widget.CloseWindow(); // Progress panel
						afterInstall(destPath);
					});
				}) { IsBackground = true };
				t.Start();
			}
			catch (Exception)
			{
				onError("Invalid mix file");
			}
		}
	}
}
