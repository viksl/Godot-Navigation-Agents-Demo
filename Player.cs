using Godot;

namespace NavigationSwarmTest;

public partial class Player : CharacterBody3D
{
    [Export]
    private float _speed = 5f;
    
    private Vector3 _velocity = Vector3.Zero;
    private Vector3 _translationVelocity = Vector3.Zero;
    
    public override void _PhysicsProcess(double delta)
    {
        base._PhysicsProcess(delta);

        var input = Input.GetVector(
            "StrafeLeft", "StrafeRight", "Forward", "Backward"
        );

        if (input == Vector2.Zero)
        {
            _translationVelocity = _velocity.MoveToward(Vector3.Zero, _speed);
        }
        else
        {
            _translationVelocity = GlobalBasis * new Vector3(input.X, 0, input.Y) * _speed;
        }
        
        _velocity = _translationVelocity;
        Velocity = _velocity;
        // RotateToVelocity(fDelta);
        MoveAndSlide();
    }
}