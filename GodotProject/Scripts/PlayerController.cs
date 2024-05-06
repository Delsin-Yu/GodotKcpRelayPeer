using System;
using Godot;
using Helpers;

namespace GodotNetworkExperiment;

public partial class PlayerController : CharacterBody2D
{
    [Export] private Vector2I CurrentDirection = Vector2I.Down;
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
        UpdateAnimation();
    }
    
    public override void _PhysicsProcess(double delta)
    {
        if (!_isPlayerControllerCharacter)
        {
            UpdateAnimation();
            return;
        }
        
        CurrentDirection = Vector2I.Zero;
        
        if (Input.IsActionPressed("ui_left")) CurrentDirection += Vector2I.Left;
        if (Input.IsActionPressed("ui_right")) CurrentDirection += Vector2I.Right;
        if (Input.IsActionPressed("ui_up")) CurrentDirection += Vector2I.Up;
        if (Input.IsActionPressed("ui_down")) CurrentDirection += Vector2I.Down;
        MoveAndCollide(((Vector2)CurrentDirection).Normalized() * _speed * (float)delta);
        
        UpdateAnimation();
    }


    private enum Direction
    {
        Up,
        Down,
        Left,
        Right
    }
    
    private void UpdateAnimation()
    {
        if(_lastDirection == CurrentDirection) return;
        _lastDirection = CurrentDirection;
        
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
            CurrentDirection = _lastDirection;
        }
        else
        {
            state = GetDirection(CurrentDirection) switch
            {
                Direction.Up => Names.IdleUp,
                Direction.Down => Names.IdleDown,
                Direction.Left => Names.IdleLeft,
                Direction.Right => Names.IdleRight,
                _ => throw new ArgumentOutOfRangeException(nameof(CurrentDirection), CurrentDirection, null)
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