using UnityEngine;
using System.IO;

public class LogSaver : MonoBehaviour
{
    private string logFilePath;

    void Awake()
    {
        // Путь к файлу
        logFilePath = Path.Combine(Application.persistentDataPath, "unity_log.txt");

        // Очистить старый лог при запуске (можно убрать, если надо дописывать)
        File.WriteAllText(logFilePath, "=== Log Started ===\n");

        // Подписаться на событие логов
        Application.logMessageReceived += HandleLog;
    }

    void OnDestroy()
    {
        Application.logMessageReceived -= HandleLog;
    }

    private void HandleLog(string logString, string stackTrace, LogType type)
    {
        using (StreamWriter writer = new StreamWriter(logFilePath, true))
        {
            writer.WriteLine($"{System.DateTime.Now:HH:mm:ss} [{type}] {logString}");
            if (type == LogType.Error || type == LogType.Exception)
                writer.WriteLine(stackTrace);
        }
    }
}

