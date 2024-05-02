using System;
using Godot;
using Helpers;

namespace GodotNetworkExperiment;

public partial class PlayerController : CharacterBody2D
{
    [Export] private AnimatedSprite2D _animatedSprite;
    [Export] private float _speed;
    [Export] private SpriteFrames _selfSpriteFrames;
    [Export] private SpriteFrames _otherSpriteFrames;
    [Export] private Control _youIndicator;
    
    private Vector2I _lastMovement = Vector2I.Down;
    
    private static class Names
    {
        public static readonly StringName IdleDown = "idle_down";
        public static readonly StringName IdleLeft = "idle_left";
        public static readonly StringName IdleRight = "idle_right";
        public static readonly StringName IdleUp = "idle_up";
        public static readonly StringName MoveDown = "move_down";
        public static readonly StringName MoveLeft = "move_left";
        public static readonly StringName MoveRight = "move_right";
        public static readonly StringName MoveUp = "move_up";
    }


    public int LocalId { get; set; }

    public override void _Ready()
    {
        UpdateAnimation(Vector2I.Zero);
    }

    public void UpdateVisual()
    {
        var isSelf = LocalId == Main.LocalId;
        _animatedSprite.SpriteFrames = isSelf ? _selfSpriteFrames : _otherSpriteFrames;
        _youIndicator.Visible = isSelf;
        UpdateAnimation(_lastMovement);
    }
    
    public override void _PhysicsProcess(double delta)
    {
        if (LocalId != Main.LocalId) return;
        var vector = Vector2I.Zero;
        
        if (Input.IsActionPressed("ui_left")) vector += Vector2I.Left;
        if (Input.IsActionPressed("ui_right")) vector += Vector2I.Right;
        if (Input.IsActionPressed("ui_up")) vector += Vector2I.Up;
        if (Input.IsActionPressed("ui_down")) vector += Vector2I.Down;
        MoveAndCollide(((Vector2)vector).Normalized() * _speed * (float)delta);
        UpdateAnimation(vector);
        // Rpc(MethodName.SyncState, ArgArray.Get([Position, vector]));
    }

    [Rpc]
    private void SyncState(Vector2 position, Vector2I movement)
    {
        Position = position;
        UpdateAnimation(movement);
    }

    private enum Direction
    {
        Up,
        Down,
        Left,
        Right
    }
    
    private void UpdateAnimation(Vector2I movement)
    {
        StringName state;
        
        if (movement != Vector2I.Zero)
        {
            state = GetDirection(movement) switch
            {
                Direction.Up => Names.MoveUp,
                Direction.Down => Names.MoveDown,
                Direction.Left => Names.MoveLeft,
                Direction.Right => Names.MoveRight,
                _ => throw new ArgumentOutOfRangeException(nameof(movement), movement, null)
            };
            _lastMovement = movement;
        }
        else
        {
            state = GetDirection(_lastMovement) switch
            {
                Direction.Up => Names.IdleUp,
                Direction.Down => Names.IdleDown,
                Direction.Left => Names.IdleLeft,
                Direction.Right => Names.IdleRight,
                _ => throw new ArgumentOutOfRangeException(nameof(_lastMovement), _lastMovement, null)
            };
        }
        
        _animatedSprite.Play(state);
    }   
    
    private static Direction GetDirection(Vector2I movement) =>
        movement.X switch
        {
            > 0 when movement.Y > 0 => Direction.Right,
            > 0 when movement.Y < 0 => Direction.Right,
            > 0 => Direction.Right,
            < 0 when movement.Y > 0 => Direction.Left,
            < 0 when movement.Y < 0 => Direction.Left,
            < 0 => Direction.Left,
            _ => movement.Y switch
            {
                > 0 => Direction.Down,
                < 0 => Direction.Up,
                _ => Direction.Down
            }
        };
}