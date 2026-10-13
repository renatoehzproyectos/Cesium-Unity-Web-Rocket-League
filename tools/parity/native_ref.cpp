#include "RocketSim.h"
#include "BulletCollision/CollisionShapes/btTriangleMesh.h"
#include "BulletCollision/CollisionShapes/btBvhTriangleMeshShape.h"
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <sstream>
#include <vector>
using namespace RocketSim;
int main(int argc, char** argv) {
  std::map<GameMode, std::vector<FileData>> none; RocketSim::InitFromMem(none, true);
  ArenaConfig cfg; cfg.useCustomBroadphase = false; cfg.memWeightMode = ArenaMemWeightMode::LIGHT;
  Arena* arena = Arena::Create(GameMode::THE_VOID, cfg, 120);
  Car* car = arena->AddCar(Team::BLUE, CAR_CONFIG_OCTANE);
  BallState bs; bs.pos = Vec(0,0,2e5f); arena->ball->SetState(bs);
  auto* tm = new btTriangleMesh();   // exact-hit floor like RocketSim's arena meshes (and PhysX mesh colliders)
  if (argc > 3) { std::ifstream tf(argv[3]); float p[9]; while (tf >> p[0]>>p[1]>>p[2]>>p[3]>>p[4]>>p[5]>>p[6]>>p[7]>>p[8]) tm->addTriangle(btVector3(p[0],p[1],p[2])*UU_TO_BT, btVector3(p[3],p[4],p[5])*UU_TO_BT, btVector3(p[6],p[7],p[8])*UU_TO_BT); }
  else { float E = 6000 * UU_TO_BT;
  tm->addTriangle(btVector3(-E,-E,0), btVector3(E,-E,0), btVector3(E,E,0));
  tm->addTriangle(btVector3(-E,-E,0), btVector3(E,E,0), btVector3(-E,E,0)); }
  auto* shape = new btBvhTriangleMeshShape(tm, true);
  auto* fl = arena->_AddStaticCollisionShape(shape, btVector3(0,0,0));
  fl->setFriction(RLConst::ARENA_COLLISION_BASE_FRICTION); fl->setRestitution(RLConst::ARENA_COLLISION_BASE_RESTITUTION);
  CarState st; st.pos = Vec(0,0,17); car->SetState(st);
  std::ifstream in(argv[1]); std::string line; int tick = 0;
  if (argc > 2 && argv[2][0]=='x') { auto& i = car->_rigidBody.m_invInertiaLocal; fprintf(stderr, "invInertiaLocal %.8f %.8f %.8f\n", i.x(), i.y(), i.z()); }
  while (std::getline(in, line)) {
    float v[5]; int j, b, h; sscanf(line.c_str(), "%f,%f,%f,%f,%f,%d,%d,%d", &v[0],&v[1],&v[2],&v[3],&v[4],&j,&b,&h);
    CarControls& c = car->controls; c.throttle=v[0]; c.steer=v[1]; c.pitch=v[2]; c.yaw=v[3]; c.roll=v[4]; c.jump=j; c.boost=b; c.handbrake=h;
    arena->Step(1); tick++;
    CarState s = car->GetState(); if (getenv("WC") && s.worldContact.hasContact) fprintf(stderr, "bodycontact tick %d n=(%.2f,%.2f,%.2f)\n", tick, s.worldContact.contactNormal.x,s.worldContact.contactNormal.y,s.worldContact.contactNormal.z);
    printf("%d,%.5f,%.5f,%.5f,%.5f,%.5f,%.5f,%.6f,%.6f,%.6f,%.6f,%.6f,%.6f,%.6f,%.6f,%.6f,%.4f,%d,%d\n", tick,
      s.pos.x,s.pos.y,s.pos.z, s.vel.x,s.vel.y,s.vel.z, s.angVel.x,s.angVel.y,s.angVel.z,
      s.rotMat.forward.x,s.rotMat.forward.y,s.rotMat.forward.z, s.rotMat.up.x,s.rotMat.up.y,s.rotMat.up.z, s.boost, (int)s.isOnGround, (int)s.isFlipping);
  }
}
