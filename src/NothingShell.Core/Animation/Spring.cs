namespace NothingShell.Core.Animation;

/// <summary>Molla smorzata 1D, integrata con Euler semi-implicito a sotto-passi fissi.</summary>
public sealed class Spring(double value, double stiffness = 280, double damping = 26)
{
    private const double MaxStep = 1.0 / 240;
    private const double MaxFrame = 0.25;
    private const double RestDistance = 0.01;
    private const double RestVelocity = 0.01;

    public double Value { get; private set; } = value;
    public double Velocity { get; private set; }
    public double Target { get; set; } = value;
    public double Stiffness { get; private set; } = stiffness;
    public double Damping { get; private set; } = damping;

    /// <summary>Cambia molla a metà corsa: la velocità resta, così il movimento non ha scatti.</summary>
    public void Tune(double stiffness, double damping)
    {
        Stiffness = stiffness;
        Damping = damping;
    }

    public bool IsSettled => Math.Abs(Value - Target) < RestDistance && Math.Abs(Velocity) < RestVelocity;

    /// <summary>Avanza di <paramref name="dt"/> secondi. Restituisce true se è ancora in movimento.</summary>
    public bool Step(double dt)
    {
        var remaining = Math.Min(dt, MaxFrame); // niente salti dopo una pausa lunga
        while (remaining > 0)
        {
            var h = Math.Min(remaining, MaxStep);
            var accel = -Stiffness * (Value - Target) - Damping * Velocity;
            Velocity += accel * h;
            Value += Velocity * h;
            remaining -= h;
        }

        if (!IsSettled)
            return true;
        SnapTo(Target);
        return false;
    }

    public void SnapTo(double value)
    {
        Value = Target = value;
        Velocity = 0;
    }
}
