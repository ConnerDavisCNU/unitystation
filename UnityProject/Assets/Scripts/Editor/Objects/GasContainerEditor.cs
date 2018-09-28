using System.Linq;
using Atmospherics;
using Objects;
using Tilemaps.Behaviours.Meta.Utils;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GasContainer))]
public class GasContainerEditor : Editor
{
	private int selected;
	private bool showGasMix;

	private float[] ratios;

	private GasContainer container;

	private float pressure;

	private void OnEnable()
	{
		container = (GasContainer) target;

		UpdateGasMix();

		InitRatios();
	}

	private void InitRatios()
	{
		ratios = new float[Gas.Count];

		foreach (Gas gas in Gas.All)
		{
			ratios[gas] = container.GasMix.Moles > 0 ? container.Gases[gas] / container.GasMix.Moles : 0;
		}
	}

	public override void OnInspectorGUI()
	{
		container.Opened = EditorGUILayout.Toggle("Opened", container.Opened);
		container.ReleasePressure = EditorGUILayout.FloatField("Release Pressure", container.ReleasePressure);

		EditorGUILayout.Space();

		container.Volume = EditorGUILayout.FloatField("Volume", container.GasMix.Volume);
		container.Temperature = EditorGUILayout.FloatField("Temperature", container.GasMix.Temperature);

		EditorGUILayout.Space();

		selected = GUILayout.Toolbar(selected, new[] {"Absolute", "Ratios"});

		if (selected == 0)
		{
			AbsolutSelection();
		}
		else
		{
			RatioSelection();
		}

		UpdateGasMix();

		EditorUtility.SetDirty(container);
	}

	private void UpdateGasMix()
	{
		container.GasMix = new GasMix(container.Gases, container.Temperature, container.Volume);
	}

	private void AbsolutSelection()
	{
		EditorGUILayout.LabelField("Moles", $"{container.GasMix.Moles}");
		container.Gases = ShowGasValues(container.GasMix.Gases);

		pressure = GasMixUtils.CalcPressure(container.Volume, container.GasMix.Moles, container.Temperature);

		EditorGUILayout.LabelField("Pressure", $"{pressure}");
	}

	private void RatioSelection()
	{
		pressure = EditorGUILayout.FloatField("Pressure", container.GasMix.Pressure);

		float moles = GasMixUtils.CalcMoles(pressure, container.GasMix.Volume, container.GasMix.Temperature);

		ratios = ShowGasValues(ratios, "Ratios");

		float total = ratios.Sum();

		foreach (Gas gas in Gas.All)
		{
			container.Gases[gas] = total > 0 ? ratios[gas] / total * moles : 0;
		}
	}

	private float[] ShowGasValues(float[] values, string label=null)
	{
		float[] result = new float[Gas.Count];

		if (label != null)
		{
			EditorGUILayout.LabelField(label);
		}

		EditorGUI.indentLevel++;
		foreach (Gas gas in Gas.All)
		{
			result[gas] = EditorGUILayout.FloatField(gas.Name, values[gas]);
		}

		EditorGUI.indentLevel--;

		return result;
	}
}