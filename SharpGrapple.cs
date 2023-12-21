
using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SharpGrapple
{
    public class PlayerGrappleInfo
    {
        public bool IsPlayerGrappling { get; set; }
        public Vector? GrappleRaycast { get; set; }
        public bool GrappleBeamSpawned { get; set; }
        public CBeam? GrappleWire { get; set; }
    }

    [MinimumApiVersion(125)]
    public partial class SharpGrapple : BasePlugin
    {
        public override string ModuleName => "SharpGrapple";
        public override string ModuleVersion => "0.1";
        public override string ModuleAuthor => "DEAFPS https://github.com/DEAFPS/";
        private Dictionary<int, PlayerGrappleInfo> playerGrapples = new Dictionary<int, PlayerGrappleInfo>();
        private Dictionary<int, CCSPlayerController> connectedPlayers = new Dictionary<int, CCSPlayerController>();

        public void InitPlayer(CCSPlayerController player)
        { 
            if (player.IsBot || !player.IsValid)
            {
                return;
            }
            else
            {
                connectedPlayers[player.Slot] = player;
                Console.WriteLine($"Added player {player.PlayerName} with UserID {player.UserId} to connectedPlayers");

                // Initialize PlayerGrappleInfo for the player
                playerGrapples[player.Slot] = new PlayerGrappleInfo(); 
            }
        }
        public override void Load(bool hotReload)
        {
            Console.WriteLine("[SharpGrapple] Loading...");

            ConVar.Find("player_ping_token_cooldown")?.SetValue(0f);

            if (hotReload) Utilities.GetPlayers().ForEach(InitPlayer);

            RegisterEventHandler<EventPlayerConnectFull>((@event, info) =>
            {
                InitPlayer(@event.Userid);
                return HookResult.Continue;
            });

            RegisterEventHandler<EventPlayerDisconnect>((@event, info) =>
            {
                var player = @event.Userid;

                if (player.IsBot || !player.IsValid)
                {
                    return HookResult.Continue;
                }
                else
                {
                    if (connectedPlayers.TryGetValue(player.Slot, out var connectedPlayer))
                    {
                        connectedPlayers.Remove(player.Slot);
                        Console.WriteLine($"Removed player {connectedPlayer.PlayerName} with UserID {connectedPlayer.UserId} from connectedPlayers");
                    }

                    playerGrapples.Remove(player.Slot);

                    return HookResult.Continue;
                }
            });

            RegisterEventHandler<EventPlayerDeath>((@event, info) =>
            {
                DetachGrapple(@event.Userid);
                return HookResult.Continue;
            });



            RegisterEventHandler<EventRoundEnd>((@event, info) =>
            {
                Utilities.GetPlayers().ForEach(DetachGrapple);
                return HookResult.Continue;
            });

            RegisterEventHandler<EventPlayerPing>((@event, info) =>
            {
                var player = @event.Userid;

                if (player.IsBot || !player.IsValid)
                {
                    return HookResult.Continue;
                }
                else
                {
                    GrappleHandler(player, @event);
                }
                return HookResult.Continue;
            });

            RegisterListener<Listeners.OnTick>(() =>
            {
                foreach (var playerEntry in connectedPlayers)
                {
                    var player = playerEntry.Value;

                    if (player == null || !player.IsValid || player.IsBot || !player.PawnIsAlive)
                        continue;

                    if (playerGrapples.TryGetValue(player.Slot, out var grappleInfo) && grappleInfo.IsPlayerGrappling)
                    {
                        if (player == null || player.PlayerPawn == null || player.PlayerPawn?.Value?.CBodyComponent == null || !player.IsValid || !player.PawnIsAlive)
                            continue;

                        Vector? playerPosition = player.PlayerPawn?.Value.CBodyComponent?.SceneNode?.AbsOrigin;
                        QAngle? viewAngles = player.PlayerPawn?.Value?.EyeAngles;

                        if (playerPosition == null || viewAngles == null)
                            continue;

                        Vector? grappleTarget = playerGrapples[player.Slot].GrappleRaycast;
                        if (grappleTarget == null)
                        {
                            Console.WriteLine($"Skipping player {player.PlayerName} due to null grappleTarget.");
                            continue;
                        }

                        if (playerGrapples[player.Slot].GrappleWire == null)
                        {
                            playerGrapples[player.Slot].GrappleWire = Utilities.CreateEntityByName<CBeam>("beam");

                            if (playerGrapples[player.Slot].GrappleWire == null)
                            {
                                Console.WriteLine($"Failed to create beam...");
                                return;
                            }

                            var grappleWire = playerGrapples[player.Slot]?.GrappleWire;
                            if (grappleWire != null)
                            { 
                                grappleWire.Render = Color.LimeGreen;
                                grappleWire.Width = 1.5f;
                                grappleWire.EndPos.X = grappleTarget.X;
                                grappleWire.EndPos.Y = grappleTarget.Y;
                                grappleWire.EndPos.Z = grappleTarget.Z;
                                grappleWire.DispatchSpawn();
                            }
                        }


                        if (IsPlayerCloseToTarget(player, grappleTarget, playerPosition, 100))
                        {
                            DetachGrapple(player);
                            continue;
                        }

                        var angleDifference = CalculateAngleDifference(new Vector(viewAngles.X, viewAngles.Y, viewAngles.Z), grappleTarget - playerPosition);
                        if (angleDifference > 180.0f)
                        {
                            DetachGrapple(player);
                            Console.WriteLine($"Player {player.PlayerName} looked away from the grapple target.");
                            continue;
                        }

                        if (player == null || player.PlayerPawn == null || player.PlayerPawn.Value.CBodyComponent == null || !player.IsValid || !player.PawnIsAlive || grappleTarget == null || viewAngles == null)
                        {
                            Console.WriteLine($"Skipping player {player?.PlayerName} due to other nulls");
                            continue;
                        }

                        PullPlayer(player, grappleTarget, playerPosition, viewAngles);

                        if (IsPlayerCloseToTarget(player, grappleTarget, playerPosition, 100))
                        {
                            DetachGrapple(player);
                            Console.WriteLine($"Player {player.PlayerName} reached the grapple target");
                        }
                    }
                }
            });

            Console.WriteLine("[SharpGrapple] Plugin Loaded");
        }

        public void GrappleHandler(CCSPlayerController? player, EventPlayerPing ping)
        {
            if (player == null) return;

            if (!playerGrapples.ContainsKey(player.Slot))
                playerGrapples[player.Slot] = new PlayerGrappleInfo();


            DetachGrapple(player);
            playerGrapples[player.Slot].IsPlayerGrappling = true;
            playerGrapples[player.Slot].GrappleRaycast = new Vector(ping.X, ping.Y, ping.Z);
        }

        private void PullPlayer(CCSPlayerController player, Vector grappleTarget, Vector playerPosition, QAngle viewAngles)
        {
            if (!player.IsValid || !player.PawnIsAlive)
            {
                Console.WriteLine("Invalid or dead player.");
                return;
            }

            if (player.PlayerPawn?.Value?.CBodyComponent?.SceneNode == null)
            {
                Console.WriteLine("SceneNode is null. Skipping pull.");
                return;
            }

            var direction = grappleTarget - playerPosition;
            var distance = direction.Length();
            direction = new Vector(direction.X / distance, direction.Y / distance, direction.Z / distance); // Normalize manually
            float grappleSpeed = 500.0f;

            var buttons = player.Buttons;

            float adjustmentFactor = 0.5f;

            var forwardVector = CalculateForwardVector(new Vector(viewAngles.X, viewAngles.Y, viewAngles.Z));
            var rightVector = CalculateRightVector(new Vector(viewAngles.X, viewAngles.Y, viewAngles.Z));

            if ((buttons & PlayerButtons.Moveright) != 0)
            {
                direction += rightVector * adjustmentFactor;
            }
            else if ((buttons & PlayerButtons.Moveleft) != 0)
            {
                direction -= rightVector * adjustmentFactor;
            }

            direction = new Vector(direction.X / direction.Length(), direction.Y / direction.Length(), direction.Z / direction.Length());

            var newVelocity = new Vector(
                direction.X * grappleSpeed,
                direction.Y * grappleSpeed,
                direction.Z * grappleSpeed
            );

            if (player.PlayerPawn.Value.AbsVelocity != null)
            {
                player.PlayerPawn.Value.AbsVelocity.X = newVelocity.X;
                player.PlayerPawn.Value.AbsVelocity.Y = newVelocity.Y;
                player.PlayerPawn.Value.AbsVelocity.Z = newVelocity.Z;
            }
            else
            {
                Console.WriteLine("AbsVelocity is null.");
                return;
            }

            var grappleWire = playerGrapples[player.Slot].GrappleWire;
            if (grappleWire != null)
            {
                grappleWire.Teleport(playerPosition, new QAngle(0, 0, 0), new Vector(0, 0, 0));
            }
            else
            {
                Console.WriteLine("GrappleWire is null.");
            }
        }

        private Vector CalculateForwardVector(Vector viewAngles)
        {
            return new Vector(0, 0, 0);

            //float pitch = viewAngles.X * (float)Math.PI / 180.0f;
            //float yaw = viewAngles.Y * (float)Math.PI / 180.0f;

            //float x = (float)(Math.Cos(pitch) * Math.Cos(yaw));
            //float y = (float)(Math.Cos(pitch) * Math.Sin(yaw));
            //float z = (float)(-Math.Sin(pitch));

            //return new Vector(x, y, z);
        }

        private Vector CalculateRightVector(Vector viewAngles)
        { 
            float yaw = (viewAngles.Y - 90.0f) * (float)Math.PI / 180.0f;

            float x = (float)Math.Cos(yaw);
            float y = (float)Math.Sin(yaw);
            float z = 0.0f;

            return new Vector(x, y, z);
        }

        private bool IsPlayerCloseToTarget(CCSPlayerController player, Vector grappleTarget, Vector playerPosition, float thresholdDistance)
        {
            var direction = grappleTarget - playerPosition;
            var distance = direction.Length();

            return distance < thresholdDistance;
        }

        private void DetachGrapple(CCSPlayerController player)
        { 
            if (playerGrapples.TryGetValue(player.Slot, out var grappleInfo))
            {
                grappleInfo.IsPlayerGrappling = false;
                grappleInfo.GrappleRaycast = null;

                if (grappleInfo.GrappleWire != null)
                {
                    grappleInfo?.GrappleWire.Remove();
                    grappleInfo!.GrappleWire = null;
                }
            }
        }

        private float CalculateAngleDifference(Vector angles1, Vector angles2)
        {
            float pitchDiff = Math.Abs(angles1.X - angles2.X);
            float yawDiff = Math.Abs(angles1.Y - angles2.Y);

            pitchDiff = pitchDiff > 180.0f ? 360.0f - pitchDiff : pitchDiff;
            yawDiff = yawDiff > 180.0f ? 360.0f - yawDiff : yawDiff;

            return Math.Max(pitchDiff, yawDiff);
        }
    }
}
