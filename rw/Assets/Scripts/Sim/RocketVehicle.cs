using System;

namespace RocketWorld.Sim
{
    /// <summary>
    /// Line-by-line port of RocketSim's Car (Car.cpp) + btVehicleRL (btVehicleRL.cpp) onto an abstract rigid body, so the SAME vehicle
    /// model can drive Unity's PhysX Rigidbody. The caller owns the physics step:
    ///   PreTick(dt) -> engine step (gravity, forces, contacts) -> PostTick(dt) -> FinishTick()
    /// exactly the order of Arena::Step. Everything is in RocketSim space (X forward, Y right, Z up, Bullet units = metres).
    /// </summary>
    public sealed class RocketVehicle
    {
        sealed class Wheel
        {
            // construction
            public Vec3 connectionCS, directionCS, axleCS;
            public float restLength, radius, stiffness, dampCompression, dampRelaxation, maxTravelCm, forceScale;
            public bool isFront;
            // control
            public float steerAngle, engineForce, brake, latFriction, longFriction;
            // raycast info
            public Vec3 hardPointWS, directionWS, axleWS, contactPointWS, contactNormalWS;
            public float suspensionLength;
            public bool isInContact, hasGround, isInContactWithWorld;
            public float suspensionRelativeVelocity, clippedInvContactDotSuspension = 1, suspensionForce, extraPushback;
            public Vec3 latAxis;            // steered wheel axle (column 1 of the wheel basis)
            public Vec3 impulse;
        }

        public readonly CarConfig config;
        public CarControls controls;
        public readonly CarState state = new CarState();

        readonly IBody _body;
        readonly IRayCaster _rays;
        readonly Wheel[] _wheels = new Wheel[4];
        float _solverTimeStep = RL.InitialSolverTimeStep;   // Bullet's solverInfo.m_timeStep: still the 1/60 default during tick 0

        public int WheelCount => _wheels.Length;
        public bool WheelInContact(int i) => _wheels[i].isInContact;

        public RocketVehicle(IBody body, IRayCaster rays, CarConfig config)
        {
            _body = body; _rays = rays; this.config = config;
            var dir = new Vec3(0, 0, -1); var axle = new Vec3(0, -1, 0);
            for (int i = 0; i < 4; i++)
            {
                bool front = i < 2, left = i % 2 == 1;
                var w = front ? config.frontWheels : config.backWheels;
                var off = w.connectionPointOffset; if (left) off.y *= -1;
                float rest = w.suspensionRestLength - RL.MaxSuspensionTravel;
                _wheels[i] = new Wheel
                {
                    connectionCS = off * RL.UuToBt, directionCS = dir, axleCS = axle,
                    restLength = rest * RL.UuToBt, radius = w.wheelRadius * RL.UuToBt,
                    stiffness = RL.SuspensionStiffness, dampCompression = RL.WheelsDampingCompression, dampRelaxation = RL.WheelsDampingRelaxation,
                    maxTravelCm = (RL.MaxSuspensionTravel * RL.UuToBt) * 100, isFront = front,
                    forceScale = front ? RL.SuspensionForceScaleFront : RL.SuspensionForceScaleBack,
                };
                UpdateWheelTransform(_wheels[i]);
                // btVehicleRL::addWheel -> updateWheelTransform(index) only; resetSuspension is never called by RocketSim
                _wheels[i].suspensionLength = _wheels[i].restLength;
                _wheels[i].contactNormalWS = -_wheels[i].directionWS;
            }
        }

        /// <summary>Teleport support: forget per-tick history (like Car::SetState).</summary>
        public void OnStateSet(bool resetFlags)
        {
            state.rotMat = _body.Basis;
            if (resetFlags)
            {
                var b = state.boost; var fresh = new CarState();
                state.isOnGround = fresh.isOnGround; state.hasJumped = state.hasDoubleJumped = state.hasFlipped = state.isFlipping = state.isJumping = false;
                state.isBoosting = state.isAutoFlipping = state.isSupersonic = false;
                state.jumpTime = state.flipTime = state.airTime = state.airTimeSinceJump = state.boostingTime = state.timeSinceBoosted = 0;
                state.handbrakeVal = state.supersonicTime = state.autoFlipTimer = state.autoFlipTorqueScale = 0;
                state.flipRelTorque = Vec3.Zero; state.lastJump = false; state.worldHasContact = false; state.boost = b;
            }
        }

        // ---------------------------------------------------------------------------------------------- helpers
        Vec3 Fwd => state.rotMat.c0;
        Vec3 Right => state.rotMat.c1;
        Vec3 Up => state.rotMat.c2;

        Vec3 VelocityInLocalPoint(Vec3 rel) => _body.LinearVelocity + _body.AngularVelocity.Cross(rel);

        // I_world^-1 (invInertiaWorld) * v and its inverse, symmetric: basis * diag * basis^T
        Vec3 InvInertiaWorldMul(Vec3 v) { var b = _body.Basis; return b.Mul(_body.InvInertiaLocal * b.MulT(v)); }
        Vec3 InertiaWorldMul(Vec3 v)    // = invInertiaWorld.inverse() * v
        {
            var b = _body.Basis; var inv = _body.InvInertiaLocal; var l = b.MulT(v);
            return b.Mul(new Vec3(l.x / inv.x, l.y / inv.y, l.z / inv.z));
        }
        void ApplyTorqueAcceleration(Vec3 acceleration) => _body.ApplyTorque(InertiaWorldMul(acceleration));   // applyTorque(invI.inverse() * a)

        // ---------------------------------------------------------------------------------------------- btVehicleRL
        void UpdateWheelTransformsWS(Wheel w)
        {
            w.isInContact = false; w.isInContactWithWorld = false;
            var basis = _body.Basis;
            w.hardPointWS = _body.Position + basis.Mul(w.connectionCS);
            w.directionWS = basis.Mul(w.directionCS);
            w.axleWS = basis.Mul(w.axleCS);
        }

        void UpdateWheelTransform(Wheel w)
        {
            UpdateWheelTransformsWS(w);
            var up = -w.directionWS;
            var right = w.axleWS;
            // basis2[:, rightIndex=1] = -right, steered about 'up' by steerAngle
            w.latAxis = Mat3.Rotate(-right, up, w.steerAngle);
        }

        float ResolveSingleBilateral(Vec3 contactPoint, Vec3 normal)
        {
            // body2 is the static world: invMass 0, no velocity.
            if (normal.Length2() > 1.1f) return 0;
            Vec3 rel1 = contactPoint - _body.Position;
            Vec3 vel = VelocityInLocalPoint(rel1);
            var basis = _body.Basis;
            Vec3 aJ = basis.MulT(rel1.Cross(normal));
            var inv = _body.InvInertiaLocal;
            float diag = 1f / _body.Mass + (inv * aJ).Dot(aJ);
            float relVel = normal.Dot(vel);
            const float contactDamping = 0.2f;
            return -contactDamping * relVel * (1f / diag);
        }

        float ResolveSingleCollisionStatic(Vec3 contactPos, Vec3 normal, float distance)   // applyImpulses = false
        {
            Vec3 rel1 = contactPos - _body.Position;
            float relVel = normal.Dot(VelocityInLocalPoint(rel1));
            float positionalError = RL.SolverErp * -distance / _solverTimeStep;
            float velocityError = -(1.0f + 0f) * relVel;
            Vec3 c0 = rel1.Cross(normal);
            float denom0 = 1f / _body.Mass + normal.Dot(InvInertiaWorldMul(c0).Cross(rel1));
            float jacDiagABInv = 1f / denom0;
            float normalImpulse = positionalError * jacDiagABInv + velocityError * jacDiagABInv;
            return normalImpulse < 0f ? 0f : normalImpulse;
        }

        void RayCast(Wheel w)
        {
            UpdateWheelTransformsWS(w);
            float suspensionTravel = w.maxTravelCm / 100;
            float realRayLength = w.restLength + suspensionTravel + w.radius - RL.SuspensionSubtraction;
            Vec3 source = w.hardPointWS, target = source + w.directionWS * realRayLength;
            w.contactPointWS = target; w.hasGround = false;
            Vec3 up = _body.Basis.c2;

            if (_rays.Cast(source, target, out var hit))
            {
                w.contactPointWS = hit.point;
                w.contactNormalWS = hit.normal;
                w.isInContact = true; w.isInContactWithWorld = true; w.hasGround = true;

                float wheelTraceLen = (w.hardPointWS - w.contactPointWS).Dot(up);
                w.suspensionLength = wheelTraceLen - w.radius;
                float minLen = w.restLength - suspensionTravel, maxLen = w.restLength + suspensionTravel;
                w.suspensionLength = Math.Min(Math.Max(w.suspensionLength, minLen), maxLen);

                float denominator = w.contactNormalWS.Dot(up);
                Vec3 relpos = w.contactPointWS - _body.Position;
                Vec3 velAtContact = VelocityInLocalPoint(relpos);
                float projVel = w.contactNormalWS.Dot(velAtContact);
                if (denominator > 0.1f) { float inv = 1 / denominator; w.suspensionRelativeVelocity = projVel * inv; w.clippedInvContactDotSuspension = inv; }
                else { w.suspensionRelativeVelocity = 0; w.clippedInvContactDotSuspension = 10; }

                float rayPushbackThresh = (w.restLength + w.radius) - RL.SuspensionSubtraction;   // static object
                if (wheelTraceLen < rayPushbackThresh)
                {
                    float delta = wheelTraceLen - rayPushbackThresh;
                    float result = ResolveSingleCollisionStatic(hit.point, hit.normal, delta);
                    w.extraPushback = result / _wheels.Length;
                }
            }
            else
            {
                w.suspensionLength = w.restLength + suspensionTravel;
                w.suspensionRelativeVelocity = 0;
                w.contactNormalWS = -w.directionWS;
                w.clippedInvContactDotSuspension = 1;
                w.extraPushback = 0;
            }
        }

        void CalcFrictionImpulses(float timeStep)
        {
            float frictionScale = _body.Mass / 3;
            foreach (var w in _wheels)
            {
                if (!w.hasGround) { w.impulse = Vec3.Zero; continue; }
                Vec3 axleDir = w.latAxis;
                Vec3 surfNormal = w.contactNormalWS;
                float proj = axleDir.Dot(surfNormal);
                axleDir = (axleDir - surfNormal * proj).SafeNormalized();
                Vec3 forwardDir = surfNormal.Cross(axleDir).SafeNormalized();
                float sideImpulse = ResolveSingleBilateral(w.contactPointWS, axleDir);

                float rollingFriction;
                if (w.engineForce == 0)
                {
                    if (w.brake != 0)
                    {
                        Vec3 carRel = w.contactPointWS - _body.Position;
                        Vec3 contactVel = VelocityInLocalPoint(carRel);     // ground velocity is zero
                        float relVel = contactVel.Dot(forwardDir);
                        if (timeStep > (1 / 80f))
                        {
                            float threshold = -(1 / (timeStep * 150f)) + 0.8f;
                            if (Math.Abs(relVel) < threshold) relVel = 0;
                        }
                        const float RollingFrictionScaleMagic = 113.73963f;
                        float v = -relVel * RollingFrictionScaleMagic;
                        rollingFriction = Math.Min(Math.Max(v, -w.brake), w.brake);
                    }
                    else rollingFriction = 0;
                }
                else rollingFriction = -w.engineForce / frictionScale;

                Vec3 total = forwardDir * rollingFriction * w.longFriction + axleDir * sideImpulse * w.latFriction;
                w.impulse = total * frictionScale;
            }
        }

        void UpdateVehicleFirst(float step)
        {
            foreach (var w in _wheels) UpdateWheelTransform(w);
            foreach (var w in _wheels) RayCast(w);
            CalcFrictionImpulses(step);
        }

        void UpdateSuspension(float dt)
        {
            foreach (var w in _wheels)
            {
                if (w.isInContact)
                {
                    float force = (w.restLength - w.suspensionLength) * w.stiffness * w.clippedInvContactDotSuspension;
                    float dampScale = w.suspensionRelativeVelocity < 0 ? w.dampCompression : w.dampRelaxation;
                    w.suspensionForce = force - (dampScale * w.suspensionRelativeVelocity);
                    w.suspensionForce *= w.forceScale;
                    if (w.suspensionForce < 0) w.suspensionForce = 0;
                }
                else w.suspensionForce = 0;
            }
            foreach (var w in _wheels)
            {
                if (w.suspensionForce != 0)
                {
                    Vec3 offset = w.contactPointWS - _body.Position;
                    float baseScale = (w.suspensionForce * dt) + w.extraPushback;
                    _body.ApplyImpulse(w.contactNormalWS * baseScale, offset);
                }
            }
        }

        void ApplyFrictionImpulses(float timeStep)
        {
            Vec3 upDir = _body.Basis.c2;
            foreach (var w in _wheels)
            {
                if (w.impulse.IsZero()) continue;
                Vec3 contactOffset = w.contactPointWS - _body.Position;
                float upDot = upDir.Dot(contactOffset);
                Vec3 relPos = contactOffset - upDir * upDot;
                _body.ApplyImpulse(w.impulse * timeStep, relPos);
            }
        }

        Vec3 UpwardsDirFromWheelContacts()
        {
            Vec3 sum = Vec3.Zero;
            foreach (var w in _wheels) if (w.isInContact) sum += w.contactNormalWS;
            if (sum.x == 0 && sum.y == 0 && sum.z == 0) return _body.Basis.c2;
            return sum.SafeNormalized();
        }

        float ForwardSpeed() => _body.LinearVelocity.Dot(_body.Basis.c0);

        // ---------------------------------------------------------------------------------------------- Car tick
        public void PreTick(float tickTime)
        {
            controls.ClampFix();
            UpdateVehicleFirst(tickTime);

            bool jumpPressed = controls.jump && !state.lastJump;

            int numWheelsInContact = 0;
            for (int i = 0; i < _wheels.Length; i++) { bool c = _wheels[i].isInContact; state.wheelsWithContact[i] = c; if (c) numWheelsInContact++; }
            state.isOnGround = numWheelsInContact >= 3;

            float forwardSpeedUU = ForwardSpeed() * RL.BtToUu;
            UpdateWheels(tickTime, numWheelsInContact, forwardSpeedUU);

            if (numWheelsInContact < 3) UpdateAirTorque(tickTime, numWheelsInContact == 0);
            else state.isFlipping = false;

            UpdateJump(tickTime, jumpPressed);
            UpdateAutoFlip(tickTime, jumpPressed);
            UpdateDoubleJumpOrFlip(tickTime, jumpPressed, forwardSpeedUU);

            if (controls.throttle != 0 && ((numWheelsInContact > 0 && numWheelsInContact < 4) || state.worldHasContact))
                UpdateAutoRoll(tickTime, numWheelsInContact);

            state.worldHasContact = false;

            // btVehicleRL::updateVehicleSecond
            UpdateSuspension(tickTime);
            ApplyFrictionImpulses(tickTime);

            UpdateBoost(tickTime);
        }

        public void PostTick(float tickTime)
        {
            state.rotMat = _body.Basis;
            float speedSq = (_body.LinearVelocity * RL.BtToUu).Length2();
            if (state.isSupersonic && state.supersonicTime < RL.SupersonicMaintainMaxTime)
                state.isSupersonic = speedSq >= RL.SupersonicMaintainMinSpeed * RL.SupersonicMaintainMinSpeed;
            else
                state.isSupersonic = speedSq >= RL.SupersonicStartSpeed * RL.SupersonicStartSpeed;
            if (state.isSupersonic) state.supersonicTime += tickTime; else state.supersonicTime = 0;
            state.lastJump = controls.jump;
            _solverTimeStep = tickTime;       // Bullet writes solverInfo.m_timeStep while stepping
        }

        /// <summary>Car::_FinishPhysicsTick: velocity limits.</summary>
        public void FinishTick()
        {
            Vec3 vel = _body.LinearVelocity, angVel = _body.AngularVelocity;
            float maxV = RL.CarMaxSpeed * RL.UuToBt;
            if (vel.Length2() > maxV * maxV) vel = vel.Normalized() * maxV;
            if (angVel.Length2() > RL.CarMaxAngSpeed * RL.CarMaxAngSpeed) angVel = angVel.Normalized() * RL.CarMaxAngSpeed;
            _body.LinearVelocity = vel; _body.AngularVelocity = angVel;
        }

        /// <summary>Car-vs-world contact reported by the engine during the step (Arena::_BtCallback_OnCarWorldCollision).</summary>
        public void NotifyWorldContact(Vec3 normal) { state.worldHasContact = true; state.worldContactNormal = normal; }

        // ---------------------------------------------------------------------------------------------- Car::_UpdateWheels
        static int Sgn(float v) => (v > 0 ? 1 : 0) - (v < 0 ? 1 : 0);
        static float Clamp(float v, float lo, float hi) { float a = v > lo ? v : lo; return a < hi ? a : hi; }

        void UpdateWheels(float tickTime, int numWheelsInContact, float forwardSpeedUU)
        {
            float absForwardSpeedUU = Math.Abs(forwardSpeedUU);
            bool wheelsHaveWorldContact = false;
            foreach (var w in _wheels) wheelsHaveWorldContact |= w.isInContactWithWorld;

            if (controls.handbrake) state.handbrakeVal += RL.PowerslideRiseRate * tickTime; else state.handbrakeVal -= RL.PowerslideFallRate * tickTime;
            state.handbrakeVal = Clamp(state.handbrakeVal, 0, 1);

            float realThrottle = controls.throttle, realBrake = 0;
            if (controls.boost && state.boost > 0) realThrottle = 1;

            {
                float driveSpeedScale = RL.DriveSpeedTorqueFactor.GetOutput(absForwardSpeedUU);
                float engineThrottle = realThrottle;
                if (controls.handbrake) { /* real throttle unchanged while powersliding */ }
                else
                {
                    float absThrottle = Math.Abs(realThrottle);
                    if (absThrottle >= RL.ThrottleDeadzone)
                    {
                        if (absForwardSpeedUU > RL.StoppingForwardVel && Sgn(realThrottle) != Sgn(forwardSpeedUU))
                        {
                            realBrake = 1;
                            if (absForwardSpeedUU > RL.BrakingNoThrottleSpeedThresh) engineThrottle = 0;
                        }
                    }
                    else
                    {
                        engineThrottle = 0;
                        bool fullStop = absForwardSpeedUU < RL.StoppingForwardVel;
                        realBrake = fullStop ? 1 : RL.CoastingBrakeFactor;
                    }
                }
                if (numWheelsInContact < 3) driveSpeedScale /= 4;
                float driveEngineForce = engineThrottle * (RL.ThrottleTorqueAmount * RL.UuToBt) * driveSpeedScale;
                float driveBrakeForce = realBrake * (RL.BrakeTorqueAmount * RL.UuToBt);
                foreach (var w in _wheels) { w.engineForce = driveEngineForce; w.brake = driveBrakeForce; }
            }

            {
                float steerAngle = RL.SteerAngleFromSpeed.GetOutput(absForwardSpeedUU);
                if (state.handbrakeVal != 0)
                    steerAngle += (RL.PowerslideSteerAngleFromSpeed.GetOutput(absForwardSpeedUU) - steerAngle) * state.handbrakeVal;
                steerAngle *= controls.steer;
                _wheels[0].steerAngle = steerAngle; _wheels[1].steerAngle = steerAngle;
            }

            {
                foreach (var w in _wheels)
                {
                    if (!w.hasGround) continue;
                    Vec3 vel = _body.LinearVelocity, angularVel = _body.AngularVelocity;
                    Vec3 latDir = w.latAxis;
                    Vec3 longDir = latDir.Cross(w.contactNormalWS);
                    float frictionCurveInput = 0;
                    Vec3 wheelDelta = w.hardPointWS - _body.Position;
                    Vec3 crossVec = (angularVel.Cross(wheelDelta) + vel) * RL.BtToUu;
                    float baseFriction = Math.Abs(crossVec.Dot(latDir));
                    if (baseFriction > 5) frictionCurveInput = baseFriction / (Math.Abs(crossVec.Dot(longDir)) + baseFriction);

                    float latFriction = RL.LatFriction.GetOutput(frictionCurveInput);
                    float longFriction = RL.LongFriction.GetOutput(frictionCurveInput);
                    if (state.handbrakeVal != 0)
                    {
                        float hb = state.handbrakeVal;
                        latFriction *= (RL.HandbrakeLatFrictionFactor.GetOutput(frictionCurveInput) - 1) * hb + 1;
                        longFriction *= (RL.HandbrakeLongFrictionFactor.GetOutput(frictionCurveInput) - 1) * hb + 1;
                    }
                    else longFriction = 1;

                    bool isContactSticky = realThrottle != 0;
                    if (!isContactSticky)
                    {
                        float nonSticky = RL.NonStickyFrictionFactor.GetOutput(w.contactNormalWS.z);
                        latFriction *= nonSticky; longFriction *= nonSticky;
                    }
                    w.latFriction = latFriction; w.longFriction = longFriction;
                }
            }

            if (wheelsHaveWorldContact)
            {
                Vec3 upwardsDir = UpwardsDirFromWheelContacts();
                bool fullStick = realThrottle != 0 || absForwardSpeedUU > RL.StoppingForwardVel;
                float stickyForceScale = 0.5f;               // (threeWheels would be 0; only the 4-wheel cars are supported)
                if (fullStick) stickyForceScale += 1 - Math.Abs(upwardsDir.z);
                _body.ApplyCentralForce(upwardsDir * stickyForceScale * (RL.GravityZ * RL.UuToBt) * RL.CarMassBt);
            }
        }

        // ---------------------------------------------------------------------------------------------- Car::_UpdateBoost
        void UpdateBoost(float tickTime)
        {
            bool hasBoost = state.boost > 0;
            if (hasBoost)
            {
                if (state.isBoosting) state.isBoosting = controls.boost || state.boostingTime < RL.BoostMinTime;
                else if (controls.boost) state.isBoosting = true;
            }
            else state.isBoosting = false;

            if (state.isBoosting) state.boostingTime += tickTime; else state.boostingTime = 0;

            if (state.isBoosting)
            {
                state.boost = Math.Max(state.boost - RL.BoostUsedPerSecond * tickTime, 0);
                _body.ApplyCentralForce((state.isOnGround ? RL.BoostAccelGround : RL.BoostAccelAir) * RL.UuToBt * Fwd * RL.CarMassBt);
                state.timeSinceBoosted = 0;
            }
            else state.timeSinceBoosted += tickTime;     // boost recharge is disabled in the default mutator config
            state.boost = Math.Min(state.boost, RL.BoostMax);
        }

        // ---------------------------------------------------------------------------------------------- Car::_UpdateJump
        void UpdateJump(float tickTime, bool jumpPressed)
        {
            if (state.isOnGround && !state.isJumping)
            {
                if (state.hasJumped && state.jumpTime < RL.JumpMinTime + RL.JumpResetTimePad) { }
                else { state.hasJumped = false; state.jumpTime = 0; }
            }

            if (state.isJumping)
            {
                state.isJumping = state.jumpTime < RL.JumpMinTime || (controls.jump && state.jumpTime < RL.JumpMaxTime);
            }
            else if (state.isOnGround && jumpPressed)
            {
                state.isJumping = true; state.jumpTime = 0;
                _body.ApplyCentralImpulse(Up * RL.JumpImmediateForce * RL.UuToBt * RL.CarMassBt);
            }

            if (state.isJumping)
            {
                state.hasJumped = true;
                Vec3 totalJumpForce = Up * RL.JumpAccel;
                if (state.jumpTime < RL.JumpMinTime) totalJumpForce *= RL.JumpPreMinAccelScale;
                _body.ApplyCentralForce(totalJumpForce * RL.UuToBt * RL.CarMassBt);
            }

            if (state.isJumping || state.hasJumped) state.jumpTime += tickTime;
        }

        // ---------------------------------------------------------------------------------------------- Car::_UpdateAirTorque
        void UpdateAirTorque(float tickTime, bool updateAirControl)
        {
            Vec3 dirPitchRight = -Right, dirYawUp = Up, dirRollForward = -Fwd;
            bool doAirControl = false;
            if (state.isFlipping) state.isFlipping = state.hasFlipped && state.flipTime < RL.FlipTorqueTime;

            if (state.isFlipping)
            {
                Vec3 relDodgeTorque = state.flipRelTorque;
                if (!state.flipRelTorque.IsZero())
                {
                    float pitchScale = 1;
                    if (relDodgeTorque.y != 0 && controls.pitch != 0)
                        if (Sgn(relDodgeTorque.y) == Sgn(controls.pitch))
                        {
                            pitchScale = 1 - Math.Min(Math.Abs(controls.pitch), 1);
                            doAirControl = true;
                        }
                    relDodgeTorque.y *= pitchScale;
                    Vec3 dodgeTorque = relDodgeTorque * new Vec3(RL.FlipTorqueX, RL.FlipTorqueY, 0);
                    _body.ApplyTorque(InertiaWorldMul(_body.Basis.Mul(dodgeTorque)));      // invI.inverse() * basis * dodgeTorque
                }
                else doAirControl = true;
            }
            else doAirControl = true;

            doAirControl &= !state.isAutoFlipping;
            doAirControl &= updateAirControl;
            if (doAirControl)
            {
                float pitchTorqueScale = 1;
                Vec3 torque;
                if (controls.pitch != 0 || controls.yaw != 0 || controls.roll != 0)
                {
                    if (state.isFlipping) pitchTorqueScale = 0;
                    else if (state.hasFlipped)
                        if (state.flipTime < RL.FlipTorqueTime + RL.FlipPitchlockExtraTime) pitchTorqueScale = 0;
                    torque = (controls.pitch * dirPitchRight * pitchTorqueScale * RL.CarAirControlTorque.x)
                           + (controls.yaw * dirYawUp * RL.CarAirControlTorque.y)
                           + (controls.roll * dirRollForward * RL.CarAirControlTorque.z);
                }
                else torque = Vec3.Zero;

                Vec3 angVel = _body.AngularVelocity;
                float dampPitch = dirPitchRight.Dot(angVel) * RL.CarAirControlDamping.x * (1 - Math.Abs(doAirControl ? (controls.pitch * pitchTorqueScale) : 0));
                float dampYaw = dirYawUp.Dot(angVel) * RL.CarAirControlDamping.y * (1 - Math.Abs(doAirControl ? controls.yaw : 0));
                float dampRoll = dirRollForward.Dot(angVel) * RL.CarAirControlDamping.z;
                Vec3 damping = (dirYawUp * dampYaw) + (dirPitchRight * dampPitch) + (dirRollForward * dampRoll);
                _body.ApplyTorque(InertiaWorldMul((torque - damping) * RL.CarTorqueScale));
            }

            if (controls.throttle != 0)
                _body.ApplyCentralForce(Fwd * controls.throttle * RL.ThrottleAirAccel * RL.UuToBt * RL.CarMassBt);
        }

        // ---------------------------------------------------------------------------------------------- Car::_UpdateDoubleJumpOrFlip
        void UpdateDoubleJumpOrFlip(float tickTime, bool jumpPressed, float forwardSpeedUU)
        {
            float tickTimeScale = tickTime / (1 / 120f);
            if (state.isOnGround)
            {
                state.hasDoubleJumped = false; state.hasFlipped = false;
                state.airTime = 0; state.airTimeSinceJump = 0; state.flipTime = 0;
            }
            else
            {
                state.airTime += tickTime;
                if (state.hasJumped && !state.isJumping) state.airTimeSinceJump += tickTime; else state.airTimeSinceJump = 0;

                if (jumpPressed && state.airTimeSinceJump < RL.DoubleJumpMaxDelay)
                {
                    float inputMagnitude = Math.Abs(controls.yaw) + Math.Abs(controls.pitch) + Math.Abs(controls.roll);
                    bool isFlipInput = inputMagnitude >= config.dodgeDeadzone;
                    bool canUse = !state.hasDoubleJumped && !state.hasFlipped;       // unlimited flips/double jumps are off
                    if (state.isAutoFlipping) canUse = false;

                    if (canUse)
                    {
                        if (isFlipInput)
                        {
                            state.flipTime = 0; state.hasFlipped = true; state.isFlipping = true;
                            float forwardSpeedRatio = Math.Abs(forwardSpeedUU) / RL.CarMaxSpeed;
                            Vec3 dodgeDir = new Vec3(-controls.pitch, controls.yaw + controls.roll, 0);
                            if (Math.Abs(controls.yaw + controls.roll) < 0.1f && Math.Abs(controls.pitch) < 0.1f) dodgeDir = Vec3.Zero;
                            else dodgeDir = dodgeDir.SafeNormalized();

                            state.flipRelTorque = new Vec3(-dodgeDir.y / tickTimeScale, dodgeDir.x / tickTimeScale, 0);

                            if (Math.Abs(dodgeDir.x) < 0.1f) dodgeDir.x = 0;
                            if (Math.Abs(dodgeDir.y) < 0.1f) dodgeDir.y = 0;

                            if (!dodgeDir.FuzzyZero())
                            {
                                bool backwards;
                                if (Math.Abs(forwardSpeedUU) < 100.0f) backwards = dodgeDir.x < 0.0f;
                                else backwards = (dodgeDir.x >= 0.0f) != (forwardSpeedUU >= 0.0f);

                                Vec3 initialDodgeVel = dodgeDir * RL.FlipInitialVelScale;
                                float maxSpeedScaleX = backwards ? RL.FlipBackwardImpulseMaxSpeedScale : RL.FlipForwardImpulseMaxSpeedScale;
                                initialDodgeVel.x *= ((maxSpeedScaleX - 1) * forwardSpeedRatio) + 1f;
                                initialDodgeVel.y *= ((RL.FlipSideImpulseMaxSpeedScale - 1) * forwardSpeedRatio) + 1f;
                                if (backwards) initialDodgeVel.x *= RL.FlipBackwardImpulseScaleX;

                                Vec3 forwardDir2D = new Vec3(Fwd.x, Fwd.y, 0).NormalizedOrZero();
                                Vec3 rightDir2D = new Vec3(-forwardDir2D.y, forwardDir2D.x, 0f);
                                Vec3 finalDeltaVel = initialDodgeVel.x * forwardDir2D + initialDodgeVel.y * rightDir2D;
                                _body.ApplyCentralImpulse(finalDeltaVel * RL.UuToBt * RL.CarMassBt);
                            }
                        }
                        else
                        {
                            _body.ApplyCentralImpulse(Up * RL.JumpImmediateForce * RL.UuToBt * RL.CarMassBt);
                            state.hasDoubleJumped = true;
                        }
                    }
                }
            }

            if (state.isFlipping)
            {
                state.flipTime += tickTime;
                if (state.flipTime <= RL.FlipTorqueTime)
                {
                    var v = _body.LinearVelocity;
                    if (state.flipTime >= RL.FlipZDampStart && (v.z < 0 || state.flipTime < RL.FlipZDampEnd))
                    {
                        v.z *= (float)Math.Pow(1 - RL.FlipZDamp120, tickTimeScale);
                        _body.LinearVelocity = v;
                    }
                }
            }
            else if (state.hasFlipped) state.flipTime += tickTime;
        }

        // ---------------------------------------------------------------------------------------------- Car::_UpdateAutoFlip / AutoRoll
        void UpdateAutoFlip(float tickTime, bool jumpPressed)
        {
            if (jumpPressed && state.worldHasContact && state.worldContactNormal.z > RL.CarAutoflipNormzThresh)
            {
                state.rotMat.GetEulerYPR(out _, out float pitch, out float rollRaw);
                float roll = -rollRaw;                                         // Angle::FromRotMat flips pitch and roll
                float absRoll = Math.Abs(roll);
                if (absRoll > RL.CarAutoflipRollThresh)
                {
                    state.autoFlipTimer = RL.CarAutoflipTime * (absRoll / (float)Math.PI);
                    state.autoFlipTorqueScale = roll > 0 ? 1 : -1;
                    state.isAutoFlipping = true;
                    _body.ApplyCentralImpulse(-Up * RL.CarAutoflipImpulse * RL.UuToBt * RL.CarMassBt);
                }
            }

            if (state.isAutoFlipping)
            {
                if (state.autoFlipTimer <= 0) { state.isAutoFlipping = false; state.autoFlipTimer = 0; }
                else
                {
                    _body.AngularVelocity += Fwd * RL.CarAutoflipTorque * state.autoFlipTorqueScale * tickTime;
                    state.autoFlipTimer -= tickTime;
                }
            }
        }

        void UpdateAutoRoll(float tickTime, int numWheelsInContact)
        {
            Vec3 groundUpDir = numWheelsInContact > 0 ? UpwardsDirFromWheelContacts() : state.worldContactNormal;
            Vec3 groundDownDir = -groundUpDir;
            Vec3 forwardDir = Fwd, rightDir = Right;
            Vec3 crossRightDir = groundUpDir.Cross(forwardDir);
            Vec3 crossForwardDir = groundDownDir.Cross(crossRightDir);
            float rightTorqueFactor = 1 - Clamp(rightDir.Dot(crossRightDir), 0, 1);
            float forwardTorqueFactor = 1 - Clamp(forwardDir.Dot(crossForwardDir), 0, 1);
            Vec3 torqueDirRight = forwardDir * (rightDir.Dot(groundUpDir) >= 0 ? -1 : 1);
            Vec3 torqueDirForward = rightDir * (forwardDir.Dot(groundUpDir) >= 0 ? 1 : -1);
            Vec3 torqueRight = torqueDirRight * rightTorqueFactor;
            Vec3 torqueForward = torqueDirForward * forwardTorqueFactor;
            _body.ApplyCentralForce(groundDownDir * RL.CarAutorollForce * RL.UuToBt * RL.CarMassBt);
            _body.ApplyTorque(InertiaWorldMul((torqueForward + torqueRight) * RL.CarAutorollTorque));
        }
    }
}
