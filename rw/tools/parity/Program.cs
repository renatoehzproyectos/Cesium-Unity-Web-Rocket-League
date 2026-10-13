using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RocketWorld.Sim;
using RocketWorld.Sim.Tests;

// Stand-in for the engine: a Bullet-style semi-implicit rigid body (same integration order as btDiscreteDynamicsWorld),
// a flat floor at z = 0, and the scenario runner that dumps the same CSV columns as native_ref.cpp.
static class Program
{
    static int Main(string[] args)
    {
        var cfg = CarConfig.Octane();
        var body = new BulletLikeBody();
        var inertia = cfg.LocalInertia(RL.CarMassBt);
        body.invInertiaLocal = new Vec3(1 / inertia.x, 1 / inertia.y, 1 / inertia.z);
        if (args.Length > 1 && args[1] == "x") Console.Error.WriteLine($"invInertiaLocal {body.invInertiaLocal.x:0.00000000} {body.invInertiaLocal.y:0.00000000} {body.invInertiaLocal.z:0.00000000}");
        body.pos = new Vec3(0, 0, 17 * RL.UuToBt);
        IRayCaster world = args.Length > 2 ? (IRayCaster)new MeshFloor(args[2]) : new FlatFloor();
        var car = new RocketVehicle(body, world, cfg);
        car.OnStateSet(true);
        const float dt = 1f / 120f;
        var inv = CultureInfo.InvariantCulture;
        int tick = 0;
        foreach (var line in File.ReadLines(args[0]))
        {
            var p = line.Split(',');
            var c = new CarControls
            {
                throttle = float.Parse(p[0], inv), steer = float.Parse(p[1], inv), pitch = float.Parse(p[2], inv), yaw = float.Parse(p[3], inv), roll = float.Parse(p[4], inv),
                jump = p[5] == "1", boost = p[6] == "1", handbrake = p[7] == "1",
            };
            car.controls = c;
            car.PreTick(dt); body.Step(dt); car.PostTick(dt); car.FinishTick();
            tick++;
            var s = car.state;
            Console.WriteLine(string.Join(",", tick,
                F(body.pos.x * 50), F(body.pos.y * 50), F(body.pos.z * 50), F(body.vel.x * 50), F(body.vel.y * 50), F(body.vel.z * 50),
                F(body.angVel.x), F(body.angVel.y), F(body.angVel.z),
                F(body.basis.c0.x), F(body.basis.c0.y), F(body.basis.c0.z), F(body.basis.c2.x), F(body.basis.c2.y), F(body.basis.c2.z),
                F(s.boost), s.isOnGround ? 1 : 0, s.isFlipping ? 1 : 0));
        }
        return 0;
    }
    static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
}
