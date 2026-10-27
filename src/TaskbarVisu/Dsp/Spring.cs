namespace TaskbarVisu.Dsp;

/// <summary>
/// Ressort amorti critique : une valeur rejoint sa cible en courbe douce, sans rebond ni à-coup, même quand la
/// cible saute (nouvelle analyse, coup de grosse caisse). <c>omega</c> règle la vivacité : environ 4/omega secondes
/// pour arriver (omega 20 ≈ 0,2 s). Intégré par pas de 10 ms au plus, stable quelle que soit la cadence.
/// </summary>
internal static class Spring
{
    public static void Step(ref float value, ref float velocity, float target, float omega, double dt)
    {
        double total = Math.Min(dt, 0.1);
        int steps = Math.Max(1, (int)Math.Ceiling(total / 0.01));
        float h = (float)(total / steps);
        for (int s = 0; s < steps; s++)
        {
            velocity += (omega * omega * (target - value) - 2 * omega * velocity) * h;
            value += velocity * h;
        }
    }

    /// <summary>Même ressort, pour tout un tableau de valeurs (une par bande ou par colonne).</summary>
    public static void Step(float[] values, float[] velocities, float[] targets, float omega, double dt)
    {
        for (int i = 0; i < values.Length; i++) Step(ref values[i], ref velocities[i], targets[i], omega, dt);
    }
}
