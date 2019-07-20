using System;
using System.Numerics;
using System.Threading.Tasks;
using AlternateLife.RageMP.Net.Enums;
using AlternateLife.RageMP.Net.EventArgs;
using AlternateLife.RageMP.Net.Helpers.EventDispatcher;
using AlternateLife.RageMP.Net.Interfaces;
using AlternateLife.RageMP.Net.Native;
using AlternateLife.RageMP.Net.Scripting;

namespace AlternateLife.RageMP.Net.Elements.Entities
{
    internal class Colshape : Entity, IColshape
    {

        private readonly AsyncChildEventDispatcher<PlayerColshapeEventArgs> _playerEnterDispatcher;
        private readonly AsyncChildEventDispatcher<PlayerColshapeEventArgs> _playerExitDispatcher;

        public event AsyncEventHandler<PlayerColshapeEventArgs> PlayerEnter
        {
            add => _playerEnterDispatcher.Subscribe(value, out _);
            remove => _playerEnterDispatcher.Unsubscribe(value, out _);
        }

        public event AsyncEventHandler<PlayerColshapeEventArgs> PlayerExit
        {
            add => _playerExitDispatcher.Subscribe(value, out _);
            remove => _playerExitDispatcher.Unsubscribe(value, out _);
        }

        internal Colshape(IntPtr nativePointer, Plugin plugin) : base(nativePointer, plugin, EntityType.Colshape)
        {
            var events = _plugin.EventScripting;

            _playerEnterDispatcher = new AsyncChildEventDispatcher<PlayerColshapeEventArgs>(_plugin, EventType.PlayerEnterColshape,
                events.PlayerEnterColshapeDispatcher, eventArgs => Task.FromResult(eventArgs.Colshape == this));

            _playerExitDispatcher = new AsyncChildEventDispatcher<PlayerColshapeEventArgs>(_plugin, EventType.PlayerExitColshape,
                events.PlayerExitColshapeDispatcher, eventArgs => Task.FromResult(eventArgs.Colshape == this));
        }

        public ColshapeType GetShapeType()
        {
            CheckExistence();

            return (ColshapeType) Rage.Colshape.Colshape_GetShapeType(NativePointer);
        }

        public Task<ColshapeType> GetShapeTypeAsync()
        {
            return _plugin.Schedule(GetShapeType);
        }

        public bool IsPointWhithin(Vector3 position)
        {
            CheckExistence();

            return Rage.Colshape.Colshape_IsPointWithin(NativePointer, position);
        }

        public Task<bool> IsPointWhithinAsync(Vector3 position)
        {
            return _plugin.Schedule(() => IsPointWhithin(position));
        }

        public override void Destroy()
        {
            base.Destroy();

            _playerEnterDispatcher.ClearSubscriptions();
            _playerExitDispatcher.ClearSubscriptions();
        }
    }
}
