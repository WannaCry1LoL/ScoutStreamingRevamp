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
			=> ScoutStreamingRevamp.Instance.OnEquipped(__instance);

		[HarmonyPostfix]
		[HarmonyPatch(
			typeof(ProbePromptController),
			nameof(ProbePromptController.OnProbeLauncherUnequipped)
		)]
		private static void ProbePromptController_OnProbeLauncherUnequipped_Postfix(ProbePromptController __instance) 
			=> ScoutStreamingRevamp.Instance.OnUnequipped(__instance);

		[HarmonyPostfix]
		[HarmonyPatch(
			typeof(ProbePromptController),
			nameof(ProbePromptController.LateInitialize)
		)]
		private static void ProbePromptController_LateInitialize_Postfix(ProbePromptController __instance) 
			=> ScoutStreamingRevamp.Instance.InitializeProbePromptUI(__instance);

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
		// private ScreenPrompt _takeSnapshotPrompt;
		// private ScreenPrompt _snapshotCenterPrompt;
		// private ScreenPrompt _reverseCamPrompt;
		// private ScreenPrompt _forwardCamPrompt;

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
				ModHelper.Console.WriteLine(
					"ScoutStreaming: couldn't find QuantumObject.OnProbeSnapshot.",
					MessageType.Error
				);
			else 
				_snapshotMethod = AccessTools.MethodDelegate<SnapshotDelegate>(snapshotMethodInfo);

			_toggleModeCommandType = ModHelper.RebindingHelper.RegisterRebindable(
				"Scout Camera: Toggle Streaming",
				"Switches the scout camera between streaming and snapshot mode.",
				Key.G,
				GamepadBinding.DPadDown,
				false
			);

			ModHelper.Events.Scenes.OnCompleteSceneChange += OnSceneChange;
		}

		private void OnDestroy()
			=> ModHelper.Events.Scenes.OnCompleteSceneChange -= OnSceneChange;

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
			UpdateUi();

			if (!_activeProbeCamera) return;
			var owCamera = _activeProbeCamera.GetOWCamera();
			if (owCamera) owCamera.enabled = CurrentMode == CaptureMode.Streaming;
		}

		private void OnSceneChange(OWScene oldScene, OWScene newScene)
		{
			if (newScene != OWScene.SolarSystem && newScene != OWScene.EyeOfTheUniverse) return;

			_quantumObjects = FindObjectsOfType<QuantumObject>();
			_probeCameras = FindObjectsOfType<ProbeCamera>();
			_activeProbeCamera = null;

			SetupModeTogglePrompt();
		}

		public void OnEquipped(ProbePromptController controller)
		{
			_toggleModePrompt?.SetVisibility(true);
			// _takeSnapshotPrompt = controller.GetValue<ScreenPrompt>("_takeSnapshotPrompt");
			// _snapshotCenterPrompt = controller.GetValue<ScreenPrompt>("_snapshotCenterPrompt");
			// _forwardCamPrompt = controller.GetValue<ScreenPrompt>("_forwardCamPrompt");
			// _reverseCamPrompt = controller.GetValue<ScreenPrompt>("_reverseCamPrompt");
			UpdateUi();
		}
		
		public void OnUnequipped(ProbePromptController controller)
		{
			_toggleModePrompt?.SetVisibility(false);
			// _takeSnapshotPrompt = _snapshotCenterPrompt = _forwardCamPrompt = _reverseCamPrompt = null;
		}

		public void UpdateUi()
		{
			_toggleModePrompt?.SetText(PromptSwitchText);
			// _takeSnapshotPrompt?.SetText(SnapshotText);
			// _snapshotCenterPrompt?.SetText(SnapshotText);
		}
		
		public void InitializeProbePromptUI(ProbePromptController controller)
		{
			SetupModeTogglePrompt();
			OnEquipped(controller);
			UpdateUi();
			OnUnequipped(controller);
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