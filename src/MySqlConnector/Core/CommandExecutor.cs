using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector.Logging;
using MySqlConnector.Protocol.Serialization;
using MySqlConnector.Utilities;

namespace MySqlConnector.Core
{
	internal static class CommandExecutor
	{
		public static async Task<MySqlDataReader> ExecuteReaderAsync(IReadOnlyList<IMySqlCommand> commands, ICommandPayloadCreator payloadCreator, CommandBehavior behavior, IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var commandListPosition = new CommandListPosition(commands);
			var command = commands[0];

			// pre-requisite: Connection is non-null must be checked before calling this method
			var connection = command.Connection!;
			var sessionId = connection.Session.Id;

			if (Log.IsDebugEnabled())
				Log.Debug("Session{0} ExecuteReader {1} CommandCount: {2}", sessionId, ioBehavior, commands.Count);

			Dictionary<string, CachedProcedure?>? cachedProcedures = null;
			foreach (var command2 in commands)
			{
				if (command2.CommandType == CommandType.StoredProcedure)
				{
					cachedProcedures ??= new();
					var commandText = command2.CommandText!;
					if (!cachedProcedures.ContainsKey(commandText))
					{
						cachedProcedures.Add(commandText, await connection.GetCachedProcedure(commandText, revalidateMissing: false, ioBehavior, cancellationToken).ConfigureAwait(false));

						// because the connection was used to execute a MySqlDataReader with the connection's DefaultCommandTimeout,
						// we need to reapply the command's CommandTimeout (even if some of the time has elapsed)
						command.CancellableCommand.ResetCommandTimeout();
					}
				}
			}

			var writer = new ByteBufferWriter();
			// cachedProcedures will be non-null if there is a stored procedure, which is also the only time it will be read
			if (!payloadCreator.WriteQueryCommand(ref commandListPosition, cachedProcedures!, writer))
				throw new InvalidOperationException("ICommandPayloadCreator failed to write query payload");

			cancellationToken.ThrowIfCancellationRequested();

			using var payload = writer.ToPayloadData();
			connection.Session.StartQuerying(command.CancellableCommand);
			command.SetLastInsertedId(-1);
			try
			{
				await connection.Session.SendAsync(payload, ioBehavior, CancellationToken.None).ConfigureAwait(false);
				Log.Debug("Sent command {0} to mysql on Session{1}.", command.CommandText, sessionId);
				var reader = await MySqlDataReader.CreateAsync(commandListPosition, payloadCreator, cachedProcedures, command, behavior, ioBehavior, cancellationToken).ConfigureAwait(false);
				Log.Debug("Returning reader for command {0} on Session{1}.", command.CommandText, sessionId);
				return reader;
			}
			catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.QueryInterrupted && cancellationToken.IsCancellationRequested)
			{
				Log.Warn("Session{0} query was interrupted", connection.Session.Id);
				throw new OperationCanceledException(ex. Message, ex, cancellationToken);
			}
			catch (Exception ex) when (payload.Span.Length > 4_194_304 && (ex is SocketException || ex is IOException || ex is MySqlProtocolException))
			{
				// the default MySQL Server value for max_allowed_packet (in MySQL 5.7) is 4MiB: https://dev.mysql.com/doc/refman/5.7/en/server-system-variables.html#sysvar_max_allowed_packet
				// use "decimal megabytes" (to round up) when creating the exception message
				int megabytes = payload.Span.Length / 1_000_000;
				throw new MySqlException("Error submitting {0}MB packet; ensure 'max_allowed_packet' is greater than {0}MB.".FormatInvariant(megabytes), ex);
			}
			catch (Exception e)
			{
				Log.Error(e, "Exception thrown executing reader for Session{0}.", sessionId);
				throw;
			}
			finally
			{
				Log.Debug("Done executing data reader for command {0} on Session{1}. Curent Command's session is", command.CommandText, sessionId, command.Connection?.Session?.Id);
			}
		}

		static readonly IMySqlConnectorLogger Log = MySqlConnectorLogManager.CreateLogger(nameof(CommandExecutor));
	}
}
