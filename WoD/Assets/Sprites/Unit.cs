using System.Collections.Generic;
using Firebase.Database;
using UnityEngine;

public class Unit : MonoBehaviour
{
    [Header("Runtime")]
    [SerializeField] public string unitKey;   // Rifleman_0 и т.п.
    [SerializeField] public string sessionId; // текущая сессия
    [SerializeField] public bool   host;      // кому принадлежит ветка (hostArmy?)

    [Header("Stats")]
    public string unitType;
    public int health;
    public int damage;
    public float attackRange;
    public float moveSpeed;

    public void Init(string type, UnitStats stats)
    {
        unitType = type;
        health = stats.health;
        damage = stats.damage;
        attackRange = stats.attackRange;
        moveSpeed = stats.moveSpeed;
        
        
    }

    // вызываем сразу после спавна
    public async void SetFirebaseContextAndPush(string sessionId, bool host, string unitKey)
    {
        this.sessionId = sessionId;
        this.host      = host;
        this.unitKey   = unitKey;

        string branch = host ? "hostArmy" : "clientArmy";
        string path   = $"sessions/{sessionId}/{branch}/{unitKey}";

        var meta = new Dictionary<string, object>
        {
            { "sessionId", sessionId },
            { "host", host }
        };

          await FirebaseDatabase.DefaultInstance.RootReference
              .Child(path).UpdateChildrenAsync(meta);

        // При желании — сразу отправлять стартовые runtime-поля:
        // await FirebaseDatabase.DefaultInstance.RootReference
        //       .Child(path).Child("runtime")
        //       .SetValueAsync(new Dictionary<string, object> {
        //           { "x", transform.position.x },
        //           { "y", transform.position.y },
        //           { "hp", health }
        //       });
    }

    public void TakeDamage(int amount)
    {
        health -= amount;
        if (health <= 0) Destroy(gameObject);
    }
}
