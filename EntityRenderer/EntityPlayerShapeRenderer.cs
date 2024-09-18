﻿using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Vintagestory.GameContent
{
    public class EntityPlayerShapeRenderer : EntityShapeRenderer
    {
        MultiTextureMeshRef firstPersonMeshRef;
        MultiTextureMeshRef thirdPersonMeshRef;
        bool watcherRegistered;
        EntityPlayer entityPlayer;
        ModSystemFpHands modSys;

        protected bool IsSelf => entity.EntityId == capi.World.Player.Entity.EntityId;

        public override bool DisplayChatMessages => true;

        public EntityPlayerShapeRenderer(Entity entity, ICoreClientAPI api) : base(entity, api)
        {
            entityPlayer = entity as EntityPlayer;

            modSys = api.ModLoader.GetModSystem<ModSystemFpHands>();
        }

        public override void OnEntityLoaded()
        {
            base.OnEntityLoaded();
        }

        public override void TesselateShape()
        {
            var inv = entityPlayer.GetBehavior<EntityBehaviorPlayerInventory>().Inventory;
            if (inv == null) return; // Player is not fully initialized yet

            // Need to call this before tesselate or we will reference the wrong texture
            defaultTexSource = GetTextureSource();

            Tesselate();

            if (!watcherRegistered)
            {
                if (IsSelf)
                {
                    capi.Settings.Bool.AddWatcher("immersiveFpMode", (on) => {
                        entity.MarkShapeModified();
                    });
                }

                watcherRegistered = true;
            }
        }

        protected override void onMeshReady(MeshData meshData)
        {
            base.onMeshReady(meshData);
            if (!IsSelf) thirdPersonMeshRef = meshRefOpaque;
        }

        public void Tesselate()
        {
            if (!IsSelf)
            {
                base.TesselateShape();
                return;
            }

            if (!loaded) return;

            TesselateShape((meshData) => {
                disposeMeshes();

                if (capi.IsShuttingDown)
                {
                    return;
                }

                if (meshData.VerticesCount > 0)
                {
                    MeshData firstPersonMesh = meshData.EmptyClone();

                    thirdPersonMeshRef = capi.Render.UploadMultiTextureMesh(meshData);

                    if (IsSelf && capi.Settings.Bool["immersiveFpMode"])
                    {
                        HashSet<int> skipJointIds = new HashSet<int>();
                        loadJointIdsRecursive(entity.AnimManager.Animator.GetPosebyName("Neck"), skipJointIds);
                        firstPersonMesh.AddMeshData(meshData, (i) => !skipJointIds.Contains(meshData.CustomInts.Values[i * 4]));
                    }
                    else
                    {
                        HashSet<int> includeJointIds = new HashSet<int>();
                        loadJointIdsRecursive(entity.AnimManager.Animator.GetPosebyName("UpperArmL"), includeJointIds);
                        loadJointIdsRecursive(entity.AnimManager.Animator.GetPosebyName("UpperArmR"), includeJointIds);

                        firstPersonMesh.AddMeshData(meshData, (i) => includeJointIds.Contains(meshData.CustomInts.Values[i * 4]));
                    }

                    firstPersonMeshRef = capi.Render.UploadMultiTextureMesh(firstPersonMesh);
                }
            });
        }

        private void loadJointIdsRecursive(ElementPose elementPose, HashSet<int> outList)
        {
            outList.Add(elementPose.ForElement.JointId);
            foreach (var childpose in elementPose.ChildElementPoses) loadJointIdsRecursive(childpose, outList);
        }

        private void disposeMeshes()
        {
            if (firstPersonMeshRef != null)
            {
                firstPersonMeshRef.Dispose();
                firstPersonMeshRef = null;
            }
            if (thirdPersonMeshRef != null)
            {
                thirdPersonMeshRef.Dispose();
                thirdPersonMeshRef = null;
            }

            meshRefOpaque = null;
        }

        public override void RenderToGui(float dt, double posX, double posY, double posZ, float yawDelta, float size)
        {
            if (IsSelf)
            {
                meshRefOpaque = thirdPersonMeshRef;
            }

            base.RenderToGui(dt, posX, posY, posZ, yawDelta, size);
        }


        public virtual float HandRenderFov
        {
            get { return capi.Settings.Int["fpHandsFoV"] * GameMath.DEG2RAD; }
        }

        public override void DoRender2D(float dt)
        {
            if (IsSelf && capi.Render.CameraType == EnumCameraMode.FirstPerson) return;

            base.DoRender2D(dt);
        }

        protected override Vec3d getAboveHeadPosition(EntityPlayer entityPlayer)
        {
            if (IsSelf) return new Vec3d(entityPlayer.CameraPos.X + entityPlayer.LocalEyePos.X, entityPlayer.CameraPos.Y + 0.4 + entityPlayer.LocalEyePos.Y, entityPlayer.CameraPos.Z + entityPlayer.LocalEyePos.Z);

            return base.getAboveHeadPosition(entityPlayer);
        }

        public override void DoRender3DOpaque(float dt, bool isShadowPass)
        {
            if (IsSelf) entityPlayer.selfNowShadowPass = isShadowPass;

            bool isHandRender = HandRenderMode && !isShadowPass;

            loadModelMatrixForPlayer(entity, IsSelf, dt, isShadowPass);

            if (IsSelf && ((capi.Settings.Bool["immersiveFpMode"] && capi.Render.CameraType == EnumCameraMode.FirstPerson) || isShadowPass))
            {
                OriginPos.Set(0, 0, 0);
            }

            if (isHandRender && capi.HideGuis) return;

            if (isHandRender)
            {
                pMatrixNormalFov = (float[])capi.Render.CurrentProjectionMatrix.Clone();
                capi.Render.Set3DProjection(capi.Render.ShaderUniforms.ZFar, HandRenderFov);

                // Needed to reproject particles to the correct position
                pMatrixHandFov = (float[])capi.Render.CurrentProjectionMatrix.Clone();
            } else
            {
                pMatrixHandFov = null;
                pMatrixNormalFov = null;
            }

            if (isShadowPass)
            {
                DoRender3DAfterOIT(dt, true);
            }

            // This was rendered in DoRender3DAfterOIT() - WHY? It makes torches render in front of water
            if (DoRenderHeldItem && !entity.AnimManager.ActiveAnimationsByAnimCode.ContainsKey("lie") && !isSpectator)
            {
                RenderHeldItem(dt, isShadowPass, false);
                RenderHeldItem(dt, isShadowPass, true);
            }

            if (isHandRender)
            {
                if (!capi.Settings.Bool["hideFpHands"] && !entityPlayer.GetBehavior<EntityBehaviorTiredness>().IsSleeping)
                {
                    var prog = modSys.fpModeHandShader;

                    meshRefOpaque = firstPersonMeshRef;

                    prog.Use();
                    prog.Uniform("rgbaAmbientIn", capi.Render.AmbientColor);
                    prog.Uniform("rgbaFogIn", capi.Render.FogColor);
                    prog.Uniform("fogMinIn", capi.Render.FogMin);
                    prog.Uniform("fogDensityIn", capi.Render.FogDensity);
                    prog.UniformMatrix("projectionMatrix", capi.Render.CurrentProjectionMatrix);
                    prog.Uniform("alphaTest", 0.05f);
                    prog.Uniform("lightPosition", capi.Render.ShaderUniforms.LightPosition3D);
                    prog.Uniform("depthOffset", -0.3f - GameMath.Max(0, capi.Settings.Int["fieldOfView"] / 90f - 1) / 2f);

                    capi.Render.GlPushMatrix();
                    capi.Render.GlLoadMatrix(capi.Render.CameraMatrixOrigin);

                    base.DoRender3DOpaqueBatched(dt, false);

                    capi.Render.GlPopMatrix();

                    prog.Stop();
                }

                capi.Render.Reset3DProjection();
            }
        }

        protected override IShaderProgram getReadyShader()
        {
            if (!entityPlayer.selfNowShadowPass && HandRenderMode)
            {
                var prog = modSys.fpModeItemShader;
                prog.Use();
                prog.Uniform("depthOffset", -0.3f - GameMath.Max(0, capi.Settings.Int["fieldOfView"] / 90f - 1) / 2f);
                prog.Uniform("ssaoAttn", 1f);
                return prog;
            }

            return base.getReadyShader();
        }

        protected override void RenderHeldItem(float dt, bool isShadowPass, bool right)
        {
            if (IsSelf) entityPlayer.selfNowShadowPass = isShadowPass;

            if (right)
            {
                ItemSlot slot = eagent?.RightHandItemSlot;
                ItemStack stack = slot?.Itemstack;
                var tongStack = eagent?.LeftHandItemSlot?.Itemstack;
                if (stack != null && stack.Collectible.GetTemperature(entity.World, stack) > 200 && tongStack?.ItemAttributes.IsTrue("heatResistant")==true)
                {
                    AttachmentPointAndPose apap = entity.AnimManager?.Animator?.GetAttachmentPointPose("LeftHand");
                    ItemRenderInfo renderInfo = capi.Render.GetItemStackRenderInfo(slot, EnumItemRenderTarget.HandTpOff, dt);
                    if (stack.ItemAttributes != null)
                    {
                        renderInfo.Transform = stack.ItemAttributes["onTongTransform"].AsObject<ModelTransform>(renderInfo.Transform);
                    }
                    RenderItem(dt, isShadowPass, stack, apap, renderInfo);
                    return;
                }
            }

            var ishandrender = HandRenderMode;
            if ((ishandrender && !isShadowPass && !capi.Settings.Bool["hideFpHands"]) || !ishandrender)
            {
                base.RenderHeldItem(dt, isShadowPass, right);
            }
        }

        public override void DoRender3DOpaqueBatched(float dt, bool isShadowPass)
        {
            if (HandRenderMode && !isShadowPass)
            {
                return;
            }

            if (isShadowPass)
            {
                meshRefOpaque = thirdPersonMeshRef;
            }
            else
            {
                meshRefOpaque = IsSelf && player?.ImmersiveFpMode == true && player?.CameraMode == EnumCameraMode.FirstPerson ? firstPersonMeshRef : thirdPersonMeshRef;
            }

            base.DoRender3DOpaqueBatched(dt, isShadowPass);
        }

        bool HandRenderMode => IsSelf && player?.ImmersiveFpMode==false && player?.CameraMode == EnumCameraMode.FirstPerson;
        public float? HeldItemPitchFollowOverride { get; set; } = null;

        float smoothedBodyYaw;

        public void loadModelMatrixForPlayer(Entity entity, bool isSelf, float dt, bool isShadowPass)
        {
            EntityPlayer selfEplr = capi.World.Player.Entity;
            Mat4f.Identity(ModelMat);

            if (isSelf)
            {
                var tf = selfEplr.MountedOn?.RenderTransform;
                if (tf != null)
                {
                    ModelMat = Mat4f.Mul(ModelMat, ModelMat, tf.Values);
                }
            }

            if (!isSelf)
            {
                var off = GetOtherPlayerRenderOffset();
                Mat4f.Translate(ModelMat, ModelMat, off.X, off.Y, off.Z);
            }

            float rotX = entity.Properties.Client.Shape != null ? entity.Properties.Client.Shape.rotateX : 0;
            float rotY = entity.Properties.Client.Shape != null ? entity.Properties.Client.Shape.rotateY : 0;
            float rotZ = entity.Properties.Client.Shape != null ? entity.Properties.Client.Shape.rotateZ : 0;
            float bodyYaw;

            if (!isSelf || capi.World.Player.CameraMode != EnumCameraMode.FirstPerson)
            {
                float yawDist = GameMath.AngleRadDistance(bodyYawLerped, eagent.BodyYaw);
                bodyYawLerped += GameMath.Clamp(yawDist, -dt * 8, dt * 8);
                bodyYaw = bodyYawLerped;

                smoothedBodyYaw = bodyYaw;
            }
            else
            {
                bodyYaw = capi.World.Player.ImmersiveFpMode || HandRenderMode /* <- will this break something? It's needed to prevent hand rotation when mounted on elk */ ? eagent.BodyYaw : eagent.Pos.Yaw;

                float yawDist = GameMath.AngleRadDistance(smoothedBodyYaw, bodyYaw);
                smoothedBodyYaw += Math.Max(0, Math.Abs(yawDist) - 0.6f) * Math.Sign(yawDist);
                yawDist = GameMath.AngleRadDistance(smoothedBodyYaw, eagent.BodyYaw);

                smoothedBodyYaw += yawDist * Math.Min(0.1f, dt) * 11f;
            }


            float bodyPitch = entityPlayer == null ? 0 : entityPlayer.WalkPitch;
            Mat4f.RotateX(ModelMat, ModelMat, entity.Pos.Roll + rotX * GameMath.DEG2RAD);
            Mat4f.RotateY(ModelMat, ModelMat, smoothedBodyYaw + (90 + rotY) * GameMath.DEG2RAD);
            var selfSwimming = isSelf && eagent.Swimming && !capi.World.Player.ImmersiveFpMode && capi.World.Player.CameraMode == EnumCameraMode.FirstPerson;

            if (!selfSwimming && ((selfEplr?.Controls.Gliding != true && selfEplr.MountedOn == null) || capi.World.Player.ImmersiveFpMode || capi.World.Player.CameraMode != EnumCameraMode.FirstPerson))
            {
                Mat4f.RotateZ(ModelMat, ModelMat, bodyPitch + rotZ * GameMath.DEG2RAD);
            }

            Mat4f.RotateX(ModelMat, ModelMat, nowSwivelRad);


            // Rotate player hands with pitch
            if (isSelf && selfEplr != null && !capi.World.Player.ImmersiveFpMode && capi.World.Player.CameraMode == EnumCameraMode.FirstPerson && !isShadowPass)
            {
                float itemSpecificPitchFollow = eagent.RightHandItemSlot?.Itemstack?.ItemAttributes?["heldItemPitchFollow"].AsFloat(0.75f) ?? 0.75f;
                float ridingSpecificPitchFollow = eagent.MountedOn?.FpHandPitchFollow ?? 1;

                float f = selfEplr?.Controls.IsFlying != true ? HeldItemPitchFollowOverride ?? (itemSpecificPitchFollow * ridingSpecificPitchFollow) : 1f;
                Mat4f.Translate(ModelMat, ModelMat, 0f, (float)entity.LocalEyePos.Y, 0f);
                Mat4f.RotateZ(ModelMat, ModelMat, (float)(entity.Pos.Pitch - GameMath.PI) * f);
                Mat4f.Translate(ModelMat, ModelMat, 0, -(float)entity.LocalEyePos.Y, 0f);
            }


            if (isSelf && !capi.World.Player.ImmersiveFpMode && capi.World.Player.CameraMode == EnumCameraMode.FirstPerson && !isShadowPass)
            {
                Mat4f.Translate(ModelMat, ModelMat, 0, capi.Settings.Float["fpHandsYOffset"], 0);
            }

            if (selfEplr != null)
            {
                float targetIntensity = entity.WatchedAttributes.GetFloat("intoxication");
                intoxIntensity += (targetIntensity - intoxIntensity) * dt / 3;
                capi.Render.PerceptionEffects.ApplyToTpPlayer(selfEplr, ModelMat, intoxIntensity);
            }

            float scale = entity.Properties.Client.Size;
            Mat4f.Scale(ModelMat, ModelMat, new float[] { scale, scale, scale });
            Mat4f.Translate(ModelMat, ModelMat, -0.5f, 0, -0.5f);
        }


        protected Vec3f GetOtherPlayerRenderOffset()
        {
            EntityPlayer selfEplr = capi.World.Player.Entity;

            // We use special positioning code for mounted entities that are on the same mount as we are.
            // While this should not be necesssary, because the client side physics does set the entity position accordingly, it does same to create 1-frame jitter if we dont specially handle this
            var selfMountedOn = selfEplr.MountedOn?.MountSupplier;
            var heMountedOn = (entity as EntityAgent).MountedOn?.MountSupplier;

            if (selfMountedOn != null && selfMountedOn == heMountedOn)
            {
                var selfMountPos = selfEplr.MountedOn.SeatPosition;
                var heMountPos = (entity as EntityAgent).MountedOn.SeatPosition;
                return new Vec3f((float)(-selfMountPos.X + heMountPos.X), (float)(-selfMountPos.Y + heMountPos.Y), (float)(-selfMountPos.Z + heMountPos.Z));
            }
            else
            {
                return new Vec3f((float)(entity.Pos.X - selfEplr.CameraPos.X), (float)(entity.Pos.InternalY - selfEplr.CameraPos.Y), (float)(entity.Pos.Z - selfEplr.CameraPos.Z));
            }
        }


        protected override void determineSidewaysSwivel(float dt)
        {
            if (entityPlayer.MountedOn != null)
            {
                entityPlayer.sidewaysSwivelAngle = nowSwivelRad = 0;
                return;
            }

            double nowAngle = Math.Atan2(entity.Pos.Motion.Z, entity.Pos.Motion.X);
            double walkspeedsq = entity.Pos.Motion.LengthSq();

            if (walkspeedsq > 0.001 && entity.OnGround)
            {
                float angledist = GameMath.AngleRadDistance((float)prevAngleSwing, (float)nowAngle);
                nowSwivelRad -= GameMath.Clamp(angledist, -0.05f, 0.05f) * dt * 40 * (float)Math.Min(0.025f, walkspeedsq) * 80 * (eagent?.Controls.Backward == true ? -1 : 1);
                nowSwivelRad = GameMath.Clamp(nowSwivelRad, -0.3f, 0.3f);
            }

            nowSwivelRad *= Math.Min(0.99f, 1 - 0.1f * dt * 60f);
            prevAngleSwing = nowAngle;

            entityPlayer.sidewaysSwivelAngle = nowSwivelRad;
        }


        public override void Dispose()
        {
            base.Dispose();

            firstPersonMeshRef?.Dispose();
            thirdPersonMeshRef?.Dispose();
        }
    }







}