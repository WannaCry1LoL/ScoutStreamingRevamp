using System.Reflection;
using HarmonyLib;
using OWML.Common.Enums;
using OWML.ModHelper;
using OWML.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ScoutStreamingRevamp;

public delegate void SnapshotDelegate(QuantumObject quantumObject, ProbeCamera camera);

public enum CaptureMode
{
	Snapshot,
	Streaming
}

[HarmonyPatch(typeof(ProbeLauncher))]
internal static class ProbeLauncherPatches
{
	[HarmonyPostfix]
	[HarmonyPatch(nameof(ProbeLauncher.TakeSnapshotWithCamera))]
	private static void TakeSnapshotWithCamera_Postfix(ProbeCamera camera)
		=> ScoutStreamingRevamp.Instance.ActivateProbeCamera(camera);
}

[HarmonyPatch(typeof(ProbePromptController))]
internal static class ProbePromptControllerPatches
{
	[HarmonyPostfix]
	[HarmonyPatch(nameof(ProbePromptController.OnProbeLauncherEquipped))]
	private static void OnProbeLauncherEquipped_Postfix(ProbePromptController __instance)
		=> ScoutStreamingRevamp.Instance.OnEquipped(__instance);

	[HarmonyPostfix]
	[HarmonyPatch(nameof(ProbePromptController.OnProbeLauncherUnequipped))]
	private static void OnProbeLauncherUnequipped_Postfix(ProbePromptController __instance) 
		=> ScoutStreamingRevamp.Instance.OnUnequipped(__instance);

	[HarmonyPostfix]
	[HarmonyPatch(nameof(ProbePromptController.LateInitialize))]
	private static void LateInitialize_Postfix(ProbePromptController __instance) 
		=> ScoutStreamingRevamp.Instance.InitializeProbePromptUI(__instance);
}

[HarmonyPatch(typeof(SatelliteSnapshotController))]
internal static class SatelliteSnapshotControllerPatches
{
	[HarmonyPostfix]
	[HarmonyPatch(nameof(SatelliteSnapshotController.OnPressInteract))]
	private static void OnPressInteract_Postfix(SatelliteSnapshotController __instance)
		=> ScoutStreamingRevamp.Instance.OnSatelliteInteract(__instance);

	[HarmonyPostfix]
	[HarmonyPatch(nameof(SatelliteSnapshotController.TurnOffProjector))]
	private static void TurnOffProjector_Postfix(SatelliteSnapshotController __instance)
		=> ScoutStreamingRevamp.Instance.OnSatelliteExit(__instance);

	[HarmonyPostfix]
	[HarmonyPatch(nameof(SatelliteSnapshotController.RenderSnapshot))]
	private static void RenderSnapshot_Postfix(SatelliteSnapshotController __instance)
		=> ScoutStreamingRevamp.Instance.OnSatelliteRenderSnapshot(__instance);

}

public class ScoutStreamingRevamp : ModBehaviour
{
	private const InputMode RelevantInputModes =
		InputMode.Character | InputMode.ShipCockpit | InputMode.StationaryProbeLauncher | InputMode.SatelliteCam;
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
		
	// private string SnapshotText => CurrentMode == CaptureMode.Streaming ? "Start Filming" : "Take Snapshot";

	private ProbeCamera _activeProbeCamera;
	private ProbeCamera[] _probeCameras = [];
	private QuantumObject[] _quantumObjects = [];
	
	private SatelliteSnapshotController _activeSatelliteController;
	
	private SnapshotDelegate _snapshotMethod;

	private InputConsts.InputCommandType _toggleModeCommandType;
	private ScreenPrompt _toggleModePrompt;

	private Harmony _harmony;
	// private ScreenPrompt _takeSnapshotPrompt;
	// private ScreenPrompt _snapshotCenterPrompt;
	// private ScreenPrompt _reverseCamPrompt;
	// private ScreenPrompt _forwardCamPrompt;

	private void Awake()
	{
		Instance = this;
		_harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly());
	}

	private void Start()
	{
		var snapshotMethodInfo = typeof(QuantumObject).GetMethod(
			nameof(QuantumObject.OnProbeSnapshot),
			BindingFlags.NonPublic | BindingFlags.Instance
		);

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
	{
		ModHelper.Events.Scenes.OnCompleteSceneChange -= OnSceneChange;
		_harmony.UnpatchSelf();
	}

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

		if (_activeProbeCamera)
		{
			var owCamera = _activeProbeCamera.GetOWCamera();
			if (owCamera) owCamera.enabled = CurrentMode == CaptureMode.Streaming;
		}
		
		if (_activeSatelliteController)
			ApplySatelliteCaptureMode(_activeSatelliteController, CurrentMode);
	}

	private void OnSceneChange(OWScene oldScene, OWScene newScene)
	{
		if (newScene != OWScene.SolarSystem && newScene != OWScene.EyeOfTheUniverse) return;

		_quantumObjects = FindObjectsOfType<QuantumObject>();
		_probeCameras = FindObjectsOfType<ProbeCamera>();
		_activeProbeCamera = null;
		_activeSatelliteController = null;

		SetupModeTogglePrompt();
	}

	public void OnEquipped(ProbePromptController controller)
	{
		// _takeSnapshotPrompt = controller.GetValue<ScreenPrompt>("_takeSnapshotPrompt");
		// _snapshotCenterPrompt = controller.GetValue<ScreenPrompt>("_snapshotCenterPrompt");
		// _forwardCamPrompt = controller.GetValue<ScreenPrompt>("_forwardCamPrompt");
		// _reverseCamPrompt = controller.GetValue<ScreenPrompt>("_reverseCamPrompt");
		UpdateUi();
	}
		
	public void OnUnequipped(ProbePromptController controller)
	{
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
		UpdateUi();
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
		if (_toggleModePrompt == null) return;
		
		var isRelevantInputMode = OWInput.IsInputMode(RelevantInputModes);
		_toggleModePrompt?.SetVisibility(isRelevantInputMode);
		if (!isRelevantInputMode) return;
		
		var command = InputLibrary.GetInputCommand(_toggleModeCommandType);
		if (command == null || !OWInput.IsNewlyPressed(command)) return;

		CurrentMode = CurrentMode == CaptureMode.Streaming
			? CaptureMode.Snapshot
			: CaptureMode.Streaming;
	}

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
	
	public void OnSatelliteInteract(SatelliteSnapshotController controller)
	{
		if (!controller) return;
		_activeSatelliteController = controller;
		ApplySatelliteCaptureMode(controller, CurrentMode);
	}

	public void OnSatelliteExit(SatelliteSnapshotController controller)
	{
		if (!controller) return;

		if (controller._satelliteCamera) controller._satelliteCamera.enabled = false;
		if (controller._satelliteLight) controller._satelliteLight.enabled = false;
		if (controller._probeMesh) controller._probeMesh.enabled = true;

		if (_activeSatelliteController == controller)
			_activeSatelliteController = null;
	}

	public void OnSatelliteRenderSnapshot(SatelliteSnapshotController controller)
	{
		if (!controller) return;
		if (CurrentMode != CaptureMode.Streaming) return;
		if (_activeSatelliteController != controller) return;

		if (controller._probeMesh) controller._probeMesh.enabled = false;
		if (controller._satelliteLight) controller._satelliteLight.enabled = true;
	}

	private static void ApplySatelliteCaptureMode(SatelliteSnapshotController controller, CaptureMode mode)
	{
		if (!controller) return;

		var streaming = mode == CaptureMode.Streaming;

		if (controller._satelliteCamera) controller._satelliteCamera.enabled = streaming;
		if (controller._satelliteLight) controller._satelliteLight.enabled = streaming;
		if (controller._probeMesh) controller._probeMesh.enabled = !streaming;
	}
}