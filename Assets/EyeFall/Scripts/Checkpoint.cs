using UnityEngine;

public class Checkpoint : MonoBehaviour
{
    void OnTriggerEnter(Collider other)
    {
        var pc = other.GetComponent<ParkourController>();
        if (pc) pc.SetCheckpoint(transform.position, transform.eulerAngles.y);
    }
}
