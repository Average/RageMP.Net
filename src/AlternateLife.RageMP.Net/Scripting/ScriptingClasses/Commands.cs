using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AlternateLife.RageMP.Net.Attributes;
using AlternateLife.RageMP.Net.Data;
using AlternateLife.RageMP.Net.Enums;
using AlternateLife.RageMP.Net.Helpers;
using AlternateLife.RageMP.Net.Interfaces;

namespace AlternateLife.RageMP.Net.Scripting.ScriptingClasses
{
    internal class Commands : ICommands
    {
        private readonly Plugin _plugin;
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<string, CommandInformation> _commands = new ConcurrentDictionary<string, CommandInformation>();

        private readonly ConcurrentDictionary<Type, ITypeParser> _typeParsers;

        public Commands(Plugin plugin)
        {
            _plugin = plugin;
            _logger = _plugin.Logger;

            _typeParsers = new ConcurrentDictionary<Type, ITypeParser>(TypeParserRegister.GetStringParsers());
        }

        public bool DoesCommandExist(string name)
        {
            Contract.NotEmpty(name, nameof(name));

            return _commands.ContainsKey(name);
        }

        public IReadOnlyCollection<string> GetRegisteredCommands()
        {
            return _commands.Keys.ToList();
        }

        public void RemoveHandler(ICommandHandler handler)
        {
            Contract.NotNull(handler, nameof(handler));

            RemoveCommands(c => c is ReflectionCommand reflectionCommand && reflectionCommand.CommandHandler == handler);
        }

        public bool Register(string name, CommandDelegate callback)
        {
            Contract.NotEmpty(name, nameof(name));
            Contract.NotNull(callback, nameof(callback));

            var commandInfo = new DelegateCommand(name, callback);

            return _commands.TryAdd(name, commandInfo);
        }

        public void Remove(CommandDelegate callback)
        {
            Contract.NotNull(callback, nameof(callback));

            RemoveCommands(c => (c as DelegateCommand)?.CommandDelegate == callback);
        }

        private void RemoveCommands(Func<CommandInformation, bool> predicate)
        {
            foreach (var command in _commands.Reverse())
            {
                if (predicate(command.Value) == false)
                {
                    continue;
                }

                _commands.TryRemove(command.Key, out _);
            }
        }

        public void Remove(string name)
        {
            Contract.NotEmpty(name, nameof(name));

            _commands.TryRemove(name, out _);
        }

        public void RegisterHandler(ICommandHandler handler)
        {
            Contract.NotNull(handler, nameof(handler));

            var commandMethods = handler
                .GetType()
                .GetMethods()
                .Where(m => m.GetCustomAttributes(typeof(CommandAttribute), true).Any());

            foreach (var commandMethod in commandMethods)
            {
                var attribute = commandMethod.GetCustomAttribute<CommandAttribute>();

                if (string.IsNullOrEmpty(attribute.Name))
                {
                    _logger.Warn($"Skipping method {commandMethod.Name} because of invalid command name.");

                    continue;
                }

                if (commandMethod.ReturnType != typeof(Task))
                {
                    _logger.Warn($"Command {attribute.Name}: Return type {commandMethod.ReturnType} is invalid, {typeof(Task)} expected!");

                    continue;
                }

                if (commandMethod.IsStatic)
                {
                    _logger.Warn($"Command {attribute.Name}: Static methods are not allowed!");

                    continue;
                }

                CommandInformation command;
                var commandDelegate = (CommandDelegate) Delegate.CreateDelegate(typeof(CommandDelegate), handler, commandMethod, false);
                if (commandDelegate != null)
                {
                    command = new DelegateCommand(attribute.Name, commandDelegate, handler);
                }
                else
                {
                    command = new ReflectionCommand(attribute.Name, handler, commandMethod);
                }

                if (_commands.TryAdd(attribute.Name, command) == false)
                {
                    _logger.Warn($"Command {attribute.Name}: Could not register command of method \"{commandMethod.Name}\" in \"{commandMethod.DeclaringType}\", maybe command-name is already in use!");
                }
            }
        }

        private bool ProcessArguments(IReadOnlyList<string> arguments, IReadOnlyList<ParameterInfo> expectedParameters, IList<object> invokingParameters)
        {
            for (int i = 1; i < arguments.Count; i++)
            {
                var expectedParameter = expectedParameters[i];
                var expectedType = expectedParameter.ParameterType;
                var targetType = expectedType;

                if (expectedType.IsEnum)
                {
                    expectedType = typeof(Enum);
                }

                if (i >= arguments.Count)
                {
                    invokingParameters[i] = expectedParameter.DefaultValue;

                    continue;
                }

                if(_typeParsers.TryGetValue(expectedType, out var parser) == false || parser.TryParse(arguments[i], targetType, out var parsedParameter) == false)
                {
                    return false;
                }

                invokingParameters[i] = parsedParameter;
            }

            return true;
        }

        public async Task ExecuteCommand(IPlayer player, string text)
        {
            string[] commandMessage = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (commandMessage.Any() == false)
            {
                return;
            }

            var commandName = commandMessage[0];

            if (_commands.TryGetValue(commandName, out var command) == false)
            {
                OnCommandError(player, text, CommandError.CommandNotFound, $"Command {commandName} not found");

                return;
            }

            switch (command)
            {
                case ReflectionCommand reflectionCommand:
                    await ExecuteReflectionCommand(player, reflectionCommand, text);
                    break;

                case DelegateCommand delegateCommand:
                    await ExecuteDelegateCommand(player, delegateCommand, commandMessage);
                    break;

                default:
                    _logger.Warn($"Command {commandName}: Invalid command-type: {command.GetType()}");
                    break;

            }
        }

        private async Task ExecuteReflectionCommand(IPlayer player, ReflectionCommand reflectionCommand, string commandText)
        {
            var commandParameters = reflectionCommand.MethodInfo.GetParameters();
            var arguments = commandText.Split(' ', commandParameters.Length, StringSplitOptions.RemoveEmptyEntries);

            if (commandParameters.Count(x => x.HasDefaultValue == false) > arguments.Length)
            {
                OnCommandError(player, commandText, CommandError.MissingArguments, "The given command lacks arguments!");

                return;
            }

            var invokingArguments = new object[commandParameters.Length];
            invokingArguments[0] = player;

            if (ProcessArguments(arguments, commandParameters, invokingArguments) == false)
            {
                OnCommandError(player, commandText, CommandError.TypeParsingFailed, $"Type conversion failed. Command parameters are: {reflectionCommand.GetParameterList()}");

                return;
            }

            try
            {
                await reflectionCommand.Invoke(invokingArguments);
            }
            catch (Exception e)
            {
                _logger.Error($"An error occured when player {await player.GetNameAsync()} executed command: {reflectionCommand.Name}: ", e);
            }
        }

        private async Task ExecuteDelegateCommand(IPlayer player, DelegateCommand delegateCommand, string[] arguments)
        {
            try
            {
                await delegateCommand.CommandDelegate(player, arguments.Skip(1).ToArray());
            }
            catch (Exception e)
            {
                _logger.Error($"An error occured when player {await player.GetNameAsync()} executed command: {delegateCommand.Name}: ", e);
            }
        }

        protected virtual void OnCommandError(IPlayer player, string input, CommandError error, string errorMessage)
        {
            _plugin.EventScripting.DispatchPlayerCommandFailed(player, input, error, errorMessage);
        }
    }
}
