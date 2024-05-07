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


    private bool _isPlayerControllerCharacter;
    private Vector2I _lastDirection = Vector2I.Zero;

    public void InitializeVisual()
    {
        _isPlayerControllerCharacter = IsMultiplayerAuthority();
        _animatedSprite.SpriteFrames = _isPlayerControllerCharacter ? _selfSpriteFrames : _otherSpriteFrames;
        _youIndicator.Visible = _isPlayerControllerCharacter;
        UpdateAnimation(_lastDirection);
    }
    
    public override void _PhysicsProcess(double delta)
    {
        if (!_isPlayerControllerCharacter) return;
        
        var movementVector = Vector2I.Zero;
        if (Input.IsActionPressed("ui_left")) movementVector += Vector2I.Left;
        if (Input.IsActionPressed("ui_right")) movementVector += Vector2I.Right;
        if (Input.IsActionPressed("ui_up")) movementVector += Vector2I.Up;
        if (Input.IsActionPressed("ui_down")) movementVector += Vector2I.Down;
        MoveAndCollide(((Vector2)movementVector).Normalized() * _speed * (float)delta);
        UpdateAnimation(movementVector);

        Rpc(MethodName.UpdateSynchronizedParameters, ArgArray.Get([movementVector, Position]));
    }

    [Rpc]
    private void UpdateSynchronizedParameters(Vector2I movementVector, Vector2 position)
    {
        Position = position;
        UpdateAnimation(movementVector);
    }

    private enum Direction
    {
        Up, Down,
        Left, Right
    }
    
    private void UpdateAnimation(Vector2I movementVector)
    {
        if(_lastDirection == movementVector) return;
        _lastDirection = movementVector;
        
        StringName state;
        
        if (_lastDirection != Vector2I.Zero)
        {
            state = GetDirection(_lastDirection) switch
            {
                Direction.Up => Names.MoveUp,
                Direction.Down => Names.MoveDown,
                Direction.Left => Names.MoveLeft,
                Direction.Right => Names.MoveRight,
                _ => throw new ArgumentOutOfRangeException(nameof(_lastDirection), _lastDirection, null)
            };
        }
        else
        {
            state = GetDirection(_lastDirection) switch
            {
                Direction.Up => Names.IdleUp,
                Direction.Down => Names.IdleDown,
                Direction.Left => Names.IdleLeft,
                Direction.Right => Names.IdleRight,
                _ => throw new ArgumentOutOfRangeException(nameof(_lastDirection), _lastDirection, null)
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