using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using OWML.Common;
using OWML.Common.Enums;
using OWML.ModHelper;
using OWML.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

/*
 * TODO: Third mode (Fixed Update)
 * TODO: Option for framerate
 * TODO: Option to take snapshot on scout launch in streaming mode
 */

namespace ScoutStreamingRevamp;

public delegate void SnapshotDelegate(QuantumObject quantumObject, ProbeCamera camera);

public enum CaptureMode
{
	Snapshot,
	Streaming
}

public enum FixHudTintMode
{
	Never,
	Always,
	Automatic
}

// ReSharper disable InconsistentNaming

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
	[HarmonyPatch(nameof(ProbePromptController.LateInitialize))]
	private static void LateInitialize_Postfix(ProbePromptController __instance)
		=> ScoutStreamingRevamp.Instance.OnProbePromptInitialized(__instance);
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

// ReSharper enable InconsistentNaming

public class ScoutStreamingRevamp : ModBehaviour
{
	private const string ModeSettingName = "ScoutMode";
	private const string FixHudTintSettingName = "FixHudTint";
	private const string ColoredCamerasSettingName = "ColoredCameras";

	private const InputMode ProbeInputModes =
		InputMode.Character | InputMode.ShipCockpit | InputMode.StationaryProbeLauncher;

	private const InputMode SatelliteInputModes = InputMode.SatelliteCam;

	private static readonly int ColorPropertyKey = Shader.PropertyToID("_Color");
	private static readonly Color DefaultHudMaterialColor = new(1, 1, 1, 1);
	private static readonly Color FixedHudMaterialColor = new(1, 0.75f, 0.75f, 1);

	public static ScoutStreamingRevamp Instance { get; private set; }

	private CaptureMode _mode;
	private FixHudTintMode _fixHudTint;
	private bool _coloredCameras;

	private ProbeCamera[] _probeCameras = [];
	private QuantumObject[] _quantumObjects = [];
	private SatelliteSnapshotController[] _satelliteControllers = [];
	private HUDCanvas _hudCanvas;
	private ProbePromptController _probePromptController;

	private ProbeCamera _activeProbeCamera;
	private SatelliteSnapshotController _activeSatellite;

	private Harmony _harmony;
	private SnapshotDelegate _snapshotMethod;
	private InputConsts.InputCommandType _toggleModeCommandType;
	private ScreenPrompt _toggleModePrompt;

	private bool IsStreaming => _mode == CaptureMode.Streaming;

	private Color HudMaterialColor => _fixHudTint switch
	{
		FixHudTintMode.Always => FixedHudMaterialColor,
		FixHudTintMode.Automatic when _coloredCameras => FixedHudMaterialColor,
		_ => DefaultHudMaterialColor
	};

	private string TogglePromptText => IsStreaming ? "Scout Camera: Streaming" : "Scout Camera: Snapshot";

	private bool CanToggleMode =>
		(OWInput.IsInputMode(ProbeInputModes) && _probePromptController && _probePromptController.enabled)
		|| (OWInput.IsInputMode(SatelliteInputModes) && _activeSatellite);

	private void Awake()
	{
		Instance = this;
		_harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), "WannaCry.ScoutStreamingRevamp");
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

		Configure(ModHelper.Config);
	}

	private void OnDestroy()
	{
		ModHelper.Events.Scenes.OnCompleteSceneChange -= OnSceneChange;
		_harmony.UnpatchSelf();
	}

	private void Update()
	{
		UpdateModeToggle();

		if (IsStreaming && _activeProbeCamera && _activeProbeCamera.isActiveAndEnabled)
			RefreshQuantumObjects();
	}

	private void OnSceneChange(OWScene oldScene, OWScene newScene)
	{
		if (newScene != OWScene.SolarSystem && newScene != OWScene.EyeOfTheUniverse) return;

		_quantumObjects = FindObjectsOfType<QuantumObject>();
		_probeCameras = FindObjectsOfType<ProbeCamera>();
		_hudCanvas = FindObjectOfType<HUDCanvas>();
		_satelliteControllers = FindObjectsOfType<SatelliteSnapshotController>();
		_probePromptController = null;
		_activeProbeCamera = null;
		_activeSatellite = null;

		SetupModeTogglePrompt();

		ApplySettings();
	}

	public override void Configure(IModConfig config)
	{
		_mode = config.GetSettingsValue<CaptureMode>(ModeSettingName);
		_fixHudTint = config.GetSettingsValue<FixHudTintMode>(FixHudTintSettingName);
		_coloredCameras = config.GetSettingsValue<bool>(ColoredCamerasSettingName);
		ApplySettings();
	}

	private void RefreshQuantumObjects()
	{
		foreach (var quantumObject in _quantumObjects)
			if (quantumObject) _snapshotMethod(quantumObject, _activeProbeCamera);
	}
	
	private void ApplySettings()
	{
		if (_hudCanvas)
			_hudCanvas._hudProbeLauncherUI._material?.SetColor(ColorPropertyKey, HudMaterialColor);

		foreach (var camera in _probeCameras)
			if (camera) ApplyCamSettings(camera._camera);
		
		foreach (var controller in _satelliteControllers)
			if (controller) ApplyCamSettings(controller._satelliteCamera);
		
		if (_activeProbeCamera && _activeProbeCamera._camera)
			_activeProbeCamera._camera.enabled = IsStreaming;
		
		UpdateSatellite(_activeSatellite);

		_toggleModePrompt?.SetText(TogglePromptText);
	}

	private void ApplyCamSettings(OWCamera camera) => camera._postProcessingSettings?.userLutEnabled = !_coloredCameras;

	private void SetupModeTogglePrompt()
	{
		var command = InputLibrary.GetInputCommand(_toggleModeCommandType);
		if (command == null) return;

		var manager = Locator.GetPromptManager();
		if (!manager) return;

		if (_toggleModePrompt != null)
			manager.RemoveScreenPrompt(_toggleModePrompt);

		_toggleModePrompt = new ScreenPrompt(command, TogglePromptText);
		manager.AddScreenPrompt(_toggleModePrompt, PromptPosition.UpperRight);
	}

	private void UpdateModeToggle()
	{
		if (_toggleModePrompt == null) return;

		var canToggle = CanToggleMode;
		_toggleModePrompt.SetVisibility(canToggle);
		if (!canToggle) return;

		var command = InputLibrary.GetInputCommand(_toggleModeCommandType);
		if (command == null || !OWInput.IsNewlyPressed(command)) return;
		
		_mode = IsStreaming ? CaptureMode.Snapshot : CaptureMode.Streaming;
		ModHelper.Config.SetSettingsValue(ModeSettingName, _mode.GetName());
		ApplySettings();
	}

	internal void OnProbePromptInitialized(ProbePromptController controller)
	{
		_probePromptController = controller;
		SetupModeTogglePrompt();
	}

	internal void ActivateProbeCamera(ProbeCamera camera)
	{
		if (!camera) return;
		_activeProbeCamera = camera;

		foreach (var probeCamera in _probeCameras)
		{
			if (!probeCamera) continue;
			var owCamera = probeCamera.GetOWCamera();
			if (owCamera) owCamera.enabled = probeCamera == camera;
		}

		if (_mode == CaptureMode.Snapshot)
			StartCoroutine(FreezeAfterFrame(camera));
	}

	private IEnumerator FreezeAfterFrame(ProbeCamera camera)
	{
		yield return new WaitForEndOfFrame();

		if (_mode != CaptureMode.Snapshot) yield break;
		if (_activeProbeCamera != camera) yield break;

		var owCamera = camera.GetOWCamera();
		if (owCamera) owCamera.enabled = false;
	}

	internal void OnSatelliteInteract(SatelliteSnapshotController controller)
	{
		if (!controller) return;

		var previous = _activeSatellite;
		_activeSatellite = controller;

		if (previous != controller) UpdateSatellite(previous);
		UpdateSatellite(controller);
	}

	internal void OnSatelliteExit(SatelliteSnapshotController controller)
	{
		if (!controller) return;

		if (_activeSatellite == controller) _activeSatellite = null;
		UpdateSatellite(controller);
	}

	internal void OnSatelliteRenderSnapshot(SatelliteSnapshotController controller)
	{
		if (IsStreaming && controller && controller == _activeSatellite)
			UpdateSatellite(controller);
	}

	private void UpdateSatellite(SatelliteSnapshotController controller)
	{
		if (!controller) return;

		var streaming = IsStreaming && controller == _activeSatellite;

		if (controller._satelliteCamera) controller._satelliteCamera.enabled = streaming;
		if (controller._satelliteLight) controller._satelliteLight.enabled = streaming;
		if (controller._probeMesh) controller._probeMesh.enabled = !streaming;
	}
}