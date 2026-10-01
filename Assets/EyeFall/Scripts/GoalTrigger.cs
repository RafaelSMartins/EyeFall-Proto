using UnityEngine;

public class GoalTrigger : MonoBehaviour
{
    void OnTriggerEnter(Collider other)
    {
        var pc = other.GetComponent<ParkourController>();
        if (pc) pc.Finish();
    }
}
