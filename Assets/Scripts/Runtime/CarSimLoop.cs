using System.Collections.Generic;
using UnityEngine;

namespace RocketWorld
{
    /// <summary>
    /// The single fixed-tick (120 Hz) loop for every simulated car (solo: one car; multiplayer host: all players' cars + the ball).
    /// Tick order is RocketSim's Arena::Step: every car PreTick -> ball pre-tick -> ONE Physics.Simulate -> every car PostTick.
    /// Puppet cars (multiplayer clients) are never stepped.
    /// </summary>
    [DefaultExecutionOrder(-45)]
    public class CarSimLoop : MonoBehaviour
    {
        public RocketBall ball;
        const float Tick = 1f / 120f;
        float _acc;
        readonly List<RocketCar> _step = new List<RocketCar>(8);

        void Update()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            _step.Clear();
            foreach (var c in RocketCar.All) if (c != null && c.CanStep) _step.Add(c);
            if (_step.Count == 0) { _acc = 0f; return; }

            _acc += dt;
            int ticks = Mathf.Min((int)(_acc / Tick), 8);
            _acc -= ticks * Tick;
            if (_acc > Tick) _acc = 0f;                                   // spiral-of-death guard
            for (int i = 0; i < ticks; i++)
            {
                foreach (var c in _step) c.PreStep();
                if (ball != null)
                {
                    ball.BeginTick();
                    foreach (var c in _step) ball.CarPreTick(c);          // RocketSim's extra car-ball impulse goes in before the contact solve
                }
                Physics.Simulate(Tick);
                foreach (var c in _step) c.PostStep();
                if (ball != null) ball.PostTick();
            }
            float a = _acc / Tick;
            foreach (var c in _step) c.ApplyVisual(a);
            if (ball != null) ball.ApplyVisual(a);
        }
    }
}
