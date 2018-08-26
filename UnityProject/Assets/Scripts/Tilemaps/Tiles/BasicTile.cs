﻿using UnityEngine;
using UnityEngine.Tilemaps;


public abstract class BasicTile : LayerTile
{
	public bool AtmosPassable;
	public bool IsSealed;
	public bool Passable;

	public Vector3Int localPos;

	public override void RefreshTile(Vector3Int position, ITilemap tilemap)
	{
		foreach (Vector3Int p in new BoundsInt(-1, -1, 0, 3, 3, 1).allPositionsWithin)
		{
			tilemap.RefreshTile(position + p);
		}

		localPos = position;
	}

	public override bool StartUp(Vector3Int position, ITilemap tilemap, GameObject go)
	{
		localPos = position;

		Debug.Log("Add tile at  " + localPos);

		CullManager.basicTiles.Add(this);
		return base.StartUp(position, tilemap, go);
	}

	public bool IsPassable()
	{
		return Passable;
	}

	public bool IsAtmosPassable()
	{
		return AtmosPassable;
	}

	public bool IsSpace()
	{
		return IsAtmosPassable() && !IsSealed;
	}

	public void CullTile(bool isOn)
	{
		Debug.Log("CULL SETTING ON TILE: " + isOn);
	}
}
