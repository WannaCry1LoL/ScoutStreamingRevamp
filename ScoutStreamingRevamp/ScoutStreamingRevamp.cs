using System;
using System.Reflection;
using HarmonyLib;
using OWML.Common;
using OWML.Common.Enums;
using OWML.ModHelper;
using OWML.Utils;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;


namespace ScoutStreamingRevamp
{
	public enum CaptureMode
	{
		Snapshot,
		Streaming
	}

	[HarmonyPatch]
	internal static class ProbeCameraPatches
	{
		[HarmonyPostfix]
		[HarmonyPatch(
			typeof(ProbeLauncher),
			nameof(ProbeLauncher.TakeSnapshotWithCamera)
		)]
		private static void ProbeLauncher_TakeSnapshotWithCamera_Postfix(ProbeCamera camera)
			=> ScoutStreamingRevamp.Instance.HandleSnapshotTaken(camera);

		[HarmonyPostfix]
		[HarmonyPatch(
			typeof(ProbePromptController),
			nameof(ProbePromptController.OnProbeLauncherEquipped)
		)]
		private static void ProbePromptController_OnProbeLauncherEquipped_Postfix(ProbePromptController __instance)
			=> ScoutStreamingRevamp.Instance.SetTogglePromptVisibility(true);

		[HarmonyPostfix]
		[HarmonyPatch(
			typeof(ProbePromptController),
			nameof(ProbePromptController.OnProbeLauncherUnequipped)
		)]
		private static void ProbePromptController_OnProbeLauncherUnequipped_Postfix(ProbePromptController __instance) 
			=> ScoutStreamingRevamp.Instance.SetTogglePromptVisibility(false);

		[HarmonyPostfix]
		[HarmonyPatch(
			typeof(ProbePromptController),
			nameof(ProbePromptController.LateInitialize)
		)]
		private static void ProbePromptController_LateInitialize_Postfix(ProbePromptController __instance) =>
			ScoutStreamingRevamp.Instance.InitializeProbePromptUI(__instance);

		[HarmonyPostfix]
		[HarmonyPatch(
			typeof(SatelliteSnapshotController), 
			nameof(SatelliteSnapshotController.OnPressInteract)
		)]
		private static void SatelliteSnapshotController_OnPressInteract_Postfix(SatelliteSnapshotController __instance)
			=> ScoutStreamingRevamp.Instance.SetSatelliteCameraEnabled(__instance, true);

		[HarmonyPostfix]
		[HarmonyPatch(
			typeof(SatelliteSnapshotController), 
			nameof(SatelliteSnapshotController.TurnOffProjector)
		)]
		private static void SatelliteSnapshotController_TurnOffProjector_Postfix(SatelliteSnapshotController __instance)
			=> ScoutStreamingRevamp.Instance.SetSatelliteCameraEnabled(__instance, false);

		[HarmonyPostfix]
		[HarmonyPatch(
			typeof(ProbePromptController), 
			nameof(ProbePromptController.Update)
		)]
		private static void ProbePromptController_Update_Postfix(ProbePromptController __instance) 
			=> ScoutStreamingRevamp.Instance.UpdateProbePromptText(__instance);
		/*
		[HarmonyPostfix]
		[HarmonyPatch(
			typeof(ProbeLauncher), 
			nameof(ProbeLauncher.EquipTool)
		)]
		
		private static void ProbeLauncher_EquipTool_Postfix(ProbeLauncher __instance)
		{
			if (!__instance.IsEquipped()) return;

			__instance.TakeSnapshotWithCamera(__instance.GetValue<ProbeCamera>("_preLaunchCamera"));
		}
		
		[HarmonyPostfix]
		[HarmonyPatch(
			typeof(ProbeLauncher), 
			nameof(ProbeLauncher.LaunchProbe)
		)]
		private static void ProbeLauncher_LaunchProbe_Postfix(ProbeLauncher __instance)
		{
			if (!__instance.IsEquipped()) return;

			var forwardCamera = __instance.GetValue<SurveyorProbe>("_activeProbe")?.GetForwardCamera();
			__instance.TakeSnapshotWithCamera(forwardCamera);
		}
		
		[HarmonyPostfix]
		[HarmonyPatch(
			typeof(ProbeLauncher), 
			nameof(ProbeLauncher.RetrieveProbe)
		)]
		private static void ProbeLauncher_RetrieveProbe_Postfix()
			=> ScoutStreamingRevamp.Instance.Invoke(nameof(ScoutStreamingRevamp.RetakeSnapshotAfterRetrieval), 0.5f);

		[HarmonyPostfix]
		[HarmonyPatch(
			typeof(SurveyorProbe), 
			nameof(SurveyorProbe.OnAnchor)
		)]
		private static void SurveyorProbe_OnAnchor_Postfix()
		{
			foreach (var launcher in Object.FindObjectsOfType<ProbeLauncher>())
			{
				if (!launcher.IsEquipped()) continue;

				var rotatingCamera = launcher.GetValue<SurveyorProbe>("_activeProbe")?.GetRotatingCamera();
				launcher.TakeSnapshotWithCamera(rotatingCamera);
			}
		}
		*/
	}

	public class ScoutStreamingRevamp : ModBehaviour
	{
		private delegate void SnapshotDelegate(QuantumObject quantumObject, ProbeCamera camera);
		
		public static ScoutStreamingRevamp Instance { get; private set; }

		private CaptureMode CurrentMode
		{
			get;
			set
			{
				if (field == value) return;

				field = value;
				OnCaptureModeChanged();
			}
		}
		
		private string PromptSwitchText => 
			CurrentMode == CaptureMode.Streaming ? "Scout Camera: Streaming" : "Scout Camera: Snapshot";
		
		private string SnapshotText =>
			CurrentMode == CaptureMode.Streaming ? "Start Filming" : "Take Snapshot";

		private ProbeCamera _activeProbeCamera;
		private ProbeCamera[] _probeCameras = [];
		private QuantumObject[] _quantumObjects = [];

		private SnapshotDelegate _snapshotMethod;

		private InputConsts.InputCommandType _toggleModeCommandType;
		private ScreenPrompt _toggleModePrompt;

		private void Awake()
		{
			Instance = this;
			Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly());
		}

		private void Start()
		{
			var snapshotMethodInfo = typeof(QuantumObject).GetMethod(
				"OnProbeSnapshot",
				BindingFlags.NonPublic | BindingFlags.Instance
			);

			if (snapshotMethodInfo == null)
			{
				ModHelper.Console.WriteLine(
					"ScoutStreaming: couldn't find QuantumObject.OnProbeSnapshot.",
					MessageType.Error
				);
			}

			_snapshotMethod = AccessTools.MethodDelegate<SnapshotDelegate>(snapshotMethodInfo);

			_toggleModeCommandType = ModHelper.RebindingHelper.RegisterRebindable(
				"Scout Camera: Toggle Streaming",
				"Switches the scout camera between streaming and snapshot mode.",
				Key.G,
				GamepadBinding.DPadDown,
				false
			);

			ModHelper.Events.Scenes.OnCompleteSceneChange += OnCompleteSceneChange;
		}

		private void OnDestroy()
			=> ModHelper.Events.Scenes.OnCompleteSceneChange -= OnCompleteSceneChange;

		private void Update()
		{
			HandleModeToggleInput();

			if (CurrentMode != CaptureMode.Streaming) return;
			if (!_activeProbeCamera || !_activeProbeCamera.isActiveAndEnabled) return;

			RefreshQuantumObjects();
		}

		private void RefreshQuantumObjects()
		{
			if (_snapshotMethod == null) return;

			foreach (var quantumObject in _quantumObjects)
			{
				if (!quantumObject) continue;
				_snapshotMethod(quantumObject, _activeProbeCamera);
			}
		}

		private void OnCaptureModeChanged()
		{
			ModHelper.Console.WriteLine(
				$"ScoutStreaming: mode set to {CurrentMode}.", 
				MessageType.Info
			);
			_toggleModePrompt?.SetText(PromptSwitchText);

			if (!_activeProbeCamera) return;
			var owCamera = _activeProbeCamera.GetOWCamera();
			if (owCamera) owCamera.enabled = CurrentMode == CaptureMode.Streaming;
		}

		private void OnCompleteSceneChange(OWScene oldScene, OWScene newScene)
		{
			if (newScene != OWScene.SolarSystem && newScene != OWScene.EyeOfTheUniverse) return;

			_quantumObjects = FindObjectsOfType<QuantumObject>();
			_probeCameras = FindObjectsOfType<ProbeCamera>();
			_activeProbeCamera = null;

			SetupModeTogglePrompt();
		}

		public void UpdateProbePromptText(ProbePromptController controller)
		{
			if (!controller) return;

			controller.GetValue<ScreenPrompt>("_takeSnapshotPrompt")?.SetText(SnapshotText);
			controller.GetValue<ScreenPrompt>("_snapshotCenterPrompt")?.SetText(SnapshotText);
		}
		
		public void InitializeProbePromptUI(ProbePromptController controller)
		{
			UpdateProbePromptText(controller);
			SetupModeTogglePrompt();
		}
		
		private void SetupModeTogglePrompt()
		{
			var command = InputLibrary.GetInputCommand(_toggleModeCommandType);
			if (command == null) return;
			
			var manager = Locator.GetPromptManager();
			if (!manager) return;
			
			_toggleModePrompt = new ScreenPrompt(command, PromptSwitchText);
			manager.AddScreenPrompt(_toggleModePrompt, PromptPosition.UpperRight);
			_toggleModePrompt.SetVisibility(false);
		}

		private void HandleModeToggleInput()
		{
			if (_toggleModePrompt == null || !_toggleModePrompt.IsVisible()) return;
			if (!OWInput.IsInputMode(InputMode.Character | InputMode.ShipCockpit)) return;

			var command = InputLibrary.GetInputCommand(_toggleModeCommandType);
			if (command == null || !OWInput.IsNewlyPressed(command)) return;

			CurrentMode = CurrentMode == CaptureMode.Streaming
				? CaptureMode.Snapshot
				: CaptureMode.Streaming;
		}

		public void SetTogglePromptVisibility(bool isVisible) => _toggleModePrompt?.SetVisibility(isVisible);

		public void ActivateProbeCamera(ProbeCamera camera)
		{
			if (!camera) return;
			_activeProbeCamera = camera;

			foreach (var probeCamera in _probeCameras)
			{
				if (!probeCamera) continue;
				var owCamera = probeCamera.GetOWCamera();
				if (!owCamera) continue;
				owCamera.enabled = probeCamera == camera;
			}

			if (CurrentMode == CaptureMode.Snapshot)
				StartCoroutine(FreezeAfterFrame(camera));
		}

		private System.Collections.IEnumerator FreezeAfterFrame(ProbeCamera camera)
		{
			yield return new WaitForEndOfFrame();

			if (CurrentMode != CaptureMode.Snapshot) yield break;
			if (_activeProbeCamera != camera) yield break;

			var owCamera = camera.GetOWCamera();
			if (owCamera) owCamera.enabled = false;
		}
		
		public void HandleSnapshotTaken(ProbeCamera camera) => ActivateProbeCamera(camera);

		public void SetSatelliteCameraEnabled(SatelliteSnapshotController controller, bool isEnabled)
		{
			if (!controller) return;
			
			var satelliteCamera = controller.GetValue<OWCamera>("_satelliteCamera");
			if (satelliteCamera)
				satelliteCamera.enabled = isEnabled;
		}
	}
}