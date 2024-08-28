using System.Collections.Generic;
using UnityEngine;

public static class SaveManager
{
    public static void Save<T>(ICustomSerialized<T> obj) where T : class
    {
        string saveString = JsonUtility.ToJson(obj.GetSerializationData());
        PlayerPrefs.SetString(obj.PrefKey, saveString);
    }

    public static void Load<T>(ICustomSerialized<T> obj) where T : class
    {
        string saveString = PlayerPrefs.GetString(obj.PrefKey, string.Empty);
        if (!string.IsNullOrEmpty(saveString))
        {
            T serializedData = JsonUtility.FromJson<T>(saveString);
            obj.Init(serializedData);
        }
        else
            obj.Init(null);
    }

    public static void Clear()
    {
        PlayerPrefs.DeleteAll();
    }
}

public interface ICustomSerialized<T> where T : class
{
    string PrefKey { get; }
    void Init(T serializedData = null);
    T GetSerializationData();

    public void Save();
    public void Load();
}
