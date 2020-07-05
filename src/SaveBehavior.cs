﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.SandBox.CampaignBehaviors;
using TaleWorlds.Core;

namespace Pacemaker
{
	class SaveBehavior : CampaignBehaviorBase
	{
		public override void RegisterEvents()
		{
			CampaignEvents.OnNewGameCreatedEvent.AddNonSerializedListener(this, new Action<CampaignGameStarter>(OnNewGameCreated));
			CampaignEvents.OnGameEarlyLoadedEvent.AddNonSerializedListener(this, new Action<CampaignGameStarter>(OnGameEarlyLoaded));
			CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, new Action<CampaignGameStarter>(OnSessionLaunched));
		}

		public override void SyncData(IDataStore dataStore)
		{
			var trace = new List<string>();

			if (HasLoaded)
			{
				trace.Add("Saving data...");
				SavedValues.Snapshot();
			}
			else
				trace.Add("Loading saved data...");

			dataStore.SyncData("PacemakerSavedValues", ref _savedValues);

			trace.Add($"Stored values: {SavedValues}");

			if (HasLoaded)
				Main.ExternalSavedValues.Serialize();
			else
				OnLoad(isVanilla: false, trace); // Cannot be a vanilla save if SyncData was called on deserialization

			Util.EventTracer.Trace(trace);
		}

		protected void OnNewGameCreated(CampaignGameStarter starter) => HasLoaded = true;

		/* OnGameEarlyLoaded is only present so that we can still initialize when adding the mod to a save
		 * that didn't previously have it enabled (so-called "vanilla save"). This is because SyncData does
		 * not even get called during game loading for behaviors that were not previously not part of the save.
		 */
		protected void OnGameEarlyLoaded(CampaignGameStarter starter)
		{
			var trace = new List<string>();

			if (!HasLoaded) // if SyncData were to be called, it would've been by now
				OnLoad(isVanilla: true, trace);

			Util.EventTracer.Trace(trace);
		}

		/* We wait until the session fully launches before potentially printing any warning about days/season
		 * mismatch (or the popup dialog for vanilla saves).
		 */
		protected void OnSessionLaunched(CampaignGameStarter starter)
		{
			WarnDayPerSeasonMismatch();
		}

		/* Main entry point for everything we do when we've just loaded our data (or lack thereof)
		 * from a savegame.
		 */
		protected void OnLoad(bool isVanilla, List<string> trace)
		{
			if (isVanilla)
			{
				trace.Add("Loading vanilla save...");
				WasVanilla = true;
			}

			AdjustTimeParams(trace);
			AdjustPregnanciesOnLoad(trace);
			HasLoaded = true;
		}

		protected void AdjustTimeParams(List<string> trace)
		{
			var neededDps = WasVanilla ? TimeParams.OldDayPerSeason : SavedValues.DaysPerSeason;

			if (Main.TimeParam.DayPerSeason != neededDps)
			{
				trace.Add($"DaysPerSeason of {Main.TimeParam.DayPerSeason} is incorrect for this campaign. Fixing...\n");
				Main.SetTimeParams(new TimeParams(neededDps), trace);
			}
		}

		protected void WarnDayPerSeasonMismatch()
		{
			if (WasVanilla && Main.Settings.DaysPerSeason != TimeParams.OldDayPerSeason)
			{
				var inquiryData = new InquiryData(
					$"{Main.DisplayName}: {TimeParams.OldDayPerSeason} Days Per Season",
					$"NOTE: Once a campaign has been started, its 'Days Per Season' setting cannot change thereafter. Since you are " +
						$"loading a non-{Main.Name} save, your effective 'Days Per Season' setting for this campaign will be the " +
						$"vanilla {TimeParams.OldDayPerSeason} days.\n    \nAll other settings can be changed freely mid-campaign, " +
						$"and new games will of course use whatever 'Days Per Season' you've configured when you start them. Now " +
						$"go on, and get playing!",
					true,
					false,
					GameTexts.FindText("str_ok", null).ToString(),
					null,
					delegate () { },
					null);

				InformationManager.ShowInquiry(inquiryData, false);
			}

			if (Main.Settings.DaysPerSeason != Main.TimeParam.DayPerSeason)
			{
				InformationManager.DisplayMessage(new InformationMessage(
					$"{Main.DisplayName}: Using {Main.TimeParam.DayPerSeason} Days Per Season (Instead of {Main.Settings.DaysPerSeason})",
					Main.ImportantTextColor));
			}
		}

		protected void AdjustPregnanciesOnLoad(List<string> trace)
		{
			if (!Main.Settings.EnablePregnancyTweaks ||
				!Main.Settings.AdjustPregnancyDueDates ||
				(SavedValues.PregnancyDuration == 0f && !WasVanilla))
				return;

			var pregnancyModel = Campaign.Current.Models.PregnancyModel;
			var newDuration = pregnancyModel.PregnancyDurationInDays;
			var ourDuration = Main.Settings.ScaledPregnancyDuration * Main.TimeParam.DayPerYear;
			var oldDuration = (WasVanilla)
				? VanillaPregnancyDuration
				: SavedValues.PregnancyDuration;

			// Check whether our pregnancy duration is actually in force (i.e., no interference from other mods).
			if (!Util.NearEqual(ourDuration, newDuration))
			{
				trace.Add($"WARNING: {Main.Name}'s pregnancy duration setting has no effect due to at least " +
					"one other mod overriding the pregnancy model in a conflicting manner.");
				trace.Add($"Type of pregnancy model: {pregnancyModel.GetType().AssemblyQualifiedName}");
			}

			// Don't bother if the effective old and new durations barely differ if at all.
			if (Util.NearEqual(oldDuration, newDuration, 1e-3f))
				return;

			trace.Add("\nAuto-adjusting in-progress pregnancy due dates due to change in pregnancy duration...\n");

			var dueDateDelta = newDuration - oldDuration;
			trace.Add($"Prior pregnancy duration (days):   {oldDuration:F2}");
			trace.Add($"Current pregnancy duration (days): {newDuration:F2}");
			trace.Add($"Pregnancy due dates will change by {dueDateDelta:F2} days.");

			// We need to iterate over the global List<PregnancyCampaignBehavior.Pregnancy> (Pregnancy is a private
			// nested class) stored in that behavior's private instance field _heroPregnancies. We'll then need to
			// access the Pregnancy.DueDate, a private instance field, for all of those. So let's do some reflection.

			var bindingFlags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
			var pregListFI = typeof(PregnancyCampaignBehavior).GetField("_heroPregnancies", bindingFlags);

			if (pregListFI == null)
			{
				trace.Add($"Could not resolve {typeof(PregnancyCampaignBehavior).FullName}._heroPregnancies field! Aborting.");
				return;
			}

			var pregT = typeof(PregnancyCampaignBehavior).GetNestedType("Pregnancy", bindingFlags);

			if (pregT == null)
			{
				trace.Add($"Could not resolve {typeof(PregnancyCampaignBehavior).FullName}.Pregnancy type! Aborting.");
				return;
			}

			var pregDueDateFI = pregT.GetField("DueDate", bindingFlags);

			if (pregDueDateFI == null)
			{
				trace.Add($"Could not resolve {pregT.FullName}.DueDate field! Aborting.");
				return;
			}

			// OK, done setting up reflection info. Start by grabbing the instance of the behavior (gee, a public API!):
			var pregBehavior = GetCampaignBehavior<PregnancyCampaignBehavior>();

			if (pregBehavior == null)
			{
				trace.Add($"Could not find campaign behavior {typeof(PregnancyCampaignBehavior).FullName}! Aborting.");
				return;
			}

			// Now iterate over the pregnancy list:

			if (!(pregListFI.GetValue(pregBehavior) is IReadOnlyList<object> pregList))
			{
				trace.Add($"Could not access {pregListFI.Name} as IReadOnlyList<object>! Aborting.");
				return;
			}

			if (pregList.Count == 0)
			{
				trace.Add("No pregnancies are in-progress. Aborting.");
				return;
			}

			var dueDateDeltaCT = CampaignTime.Days(dueDateDelta);
			int nPregs = 0;

			foreach (var preg in pregList)
			{
				CampaignTime dueDateCT = (CampaignTime)pregDueDateFI.GetValue(preg);
				pregDueDateFI.SetValue(preg, dueDateCT + dueDateDeltaCT);
				++nPregs;
			}

			trace.Add($"Adjusted {nPregs} in-progress pregnancies.");
		}

		protected SavedValues SavedValues
		{
			get => _savedValues;
			set => _savedValues = value;
		}

		protected bool HasLoaded { get; set; }
		protected bool WasVanilla { get; set; }

		private SavedValues _savedValues = new SavedValues();
		private const float VanillaPregnancyDuration = 36f;
	}
}
