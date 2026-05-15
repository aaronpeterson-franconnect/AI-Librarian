using AiLibrarian.Api.EntraSync;
using AiLibrarian.Auditing;
using AiLibrarian.Domain;
using AiLibrarian.Domain.Users;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiLibrarian.Api.Tests.EntraSync;

/// <summary>
/// Pins the reconciliation arithmetic + the audit emission contract
/// for the Entra group-sync orchestrator. Doesn't talk to Graph or
/// Postgres — uses stub <see cref="IGraphMembershipClient"/> +
/// recording <see cref="IUserAuthorizationWriter"/> + recording
/// <see cref="IAuditWriter"/>.
/// </summary>
public sealed class EntraGroupSyncServiceTests
{
	private static readonly Guid DeptEng = Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly string GroupLibrarians = "22222222-2222-2222-2222-222222222222";
	private static readonly Guid UserAlice = Guid.Parse("33333333-3333-3333-3333-333333333333");
	private static readonly Guid UserBob = Guid.Parse("44444444-4444-4444-4444-444444444444");

	[Fact]
	public async Task Disabled_Options_Returns_NoOp_Report_And_No_Writes()
	{
		var graph = new StubGraph();
		var writer = new RecordingWriter();
		var audit = new RecordingAudit();
		var svc = MakeService(graph, writer, audit, new EntraGroupSyncOptions { Enabled = false });

		var report = await svc.RunAsync();

		report.GroupsProcessed.Should().Be(0);
		report.Mappings.Should().BeEmpty();
		writer.GrantCalls.Should().BeEmpty();
		writer.ReconcileCalls.Should().BeEmpty();
		audit.Events.Should().BeEmpty();
	}

	[Fact]
	public async Task New_Members_Get_Granted_And_Run_Audited()
	{
		var graph = new StubGraph(membership: new()
		{
			[GroupLibrarians] = new[] { UserAlice, UserBob },
		});
		var writer = new RecordingWriter
		{
			// New users -> WriteAsync returns true (insert)
			InsertResult = true,
		};
		var audit = new RecordingAudit();
		var opts = new EntraGroupSyncOptions
		{
			Enabled = true,
			GroupMappings =
			{
				new EntraGroupMapping
				{
					GroupObjectId = GroupLibrarians,
					DisplayLabel = "Engineering Librarians",
					Role = Role.Librarian,
					DepartmentId = DeptEng.ToString("D"),
				},
			},
		};

		var svc = MakeService(graph, writer, audit, opts);
		var report = await svc.RunAsync();

		report.GroupsProcessed.Should().Be(1);
		report.GroupsSucceeded.Should().Be(1);
		report.GroupsFailed.Should().Be(0);
		report.GrantsAdded.Should().Be(2);

		writer.GrantCalls.Should().HaveCount(2);
		writer.GrantCalls.Should().AllSatisfy(g =>
		{
			g.Role.Should().Be(Role.Librarian);
			g.DepartmentId.Should().Be(DeptEng);
			g.SourceGroupId.Should().StartWith("entra-sync:");
		});

		// Reconcile called with the membership list (keepUserIds).
		writer.ReconcileCalls.Should().ContainSingle();
		writer.ReconcileCalls[0].KeepUserIds.Should().BeEquivalentTo(new[] { UserAlice, UserBob });

		// Two audit rows: one per-group (grants_added=2) + one per-run.
		audit.Events.Should().HaveCount(2);
		audit.Events.Should().Contain(e => e.EventSubtype == "sync.group");
		audit.Events.Should().Contain(e => e.EventSubtype == "sync.run");
	}

	[Fact]
	public async Task Removed_Member_Triggers_Revoke_Via_Reconcile()
	{
		// The group has only Alice now; Bob was a member and is no longer.
		var graph = new StubGraph(membership: new()
		{
			[GroupLibrarians] = new[] { UserAlice },
		});
		var writer = new RecordingWriter { InsertResult = false, ReconcileResult = 1 };
		var audit = new RecordingAudit();
		var opts = new EntraGroupSyncOptions
		{
			Enabled = true,
			GroupMappings =
			{
				new EntraGroupMapping
				{
					GroupObjectId = GroupLibrarians,
					Role = Role.Librarian,
					DepartmentId = DeptEng.ToString("D"),
				},
			},
		};

		var svc = MakeService(graph, writer, audit, opts);
		var report = await svc.RunAsync();

		report.GrantsRevoked.Should().Be(1);
		writer.ReconcileCalls[0].KeepUserIds.Should().BeEquivalentTo(new[] { UserAlice });
	}

	[Fact]
	public async Task Invalid_Mapping_Is_Reported_And_Other_Mappings_Continue()
	{
		// One bad mapping (Admin with a department) + one good mapping.
		var graph = new StubGraph(membership: new()
		{
			[GroupLibrarians] = new[] { UserAlice },
		});
		var writer = new RecordingWriter { InsertResult = true };
		var audit = new RecordingAudit();

		var opts = new EntraGroupSyncOptions
		{
			Enabled = true,
			GroupMappings =
			{
				new EntraGroupMapping
				{
					// Bad: Admin must be system-wide.
					GroupObjectId = Guid.NewGuid().ToString("D"),
					Role = Role.Admin,
					DepartmentId = DeptEng.ToString("D"),
				},
				new EntraGroupMapping
				{
					GroupObjectId = GroupLibrarians,
					Role = Role.Librarian,
					DepartmentId = DeptEng.ToString("D"),
				},
			},
		};

		var svc = MakeService(graph, writer, audit, opts);
		var report = await svc.RunAsync();

		report.GroupsProcessed.Should().Be(2);
		report.GroupsFailed.Should().Be(1);
		report.GroupsSucceeded.Should().Be(1);
		report.Mappings.Should().Contain(m => m.Error != null && m.Error.Contains("system-wide"));

		// The run-level audit reflects partial outcome.
		audit.Events.Should().Contain(e => e.EventSubtype == "sync.run" && e.Outcome == EventOutcome.Partial);
	}

	[Fact]
	public async Task Graph_Failure_For_One_Group_Doesnt_Block_Others()
	{
		var workingGroup = Guid.NewGuid().ToString("D");
		var failingGroup = Guid.NewGuid().ToString("D");
		var graph = new StubGraph(membership: new()
		{
			[workingGroup] = new[] { UserAlice },
		})
		{
			ThrowingGroup = failingGroup,
		};
		var writer = new RecordingWriter { InsertResult = true };
		var audit = new RecordingAudit();

		var opts = new EntraGroupSyncOptions
		{
			Enabled = true,
			GroupMappings =
			{
				new EntraGroupMapping
				{
					GroupObjectId = failingGroup,
					Role = Role.Librarian,
					DepartmentId = DeptEng.ToString("D"),
				},
				new EntraGroupMapping
				{
					GroupObjectId = workingGroup,
					Role = Role.Reader,
					DepartmentId = DeptEng.ToString("D"),
				},
			},
		};

		var svc = MakeService(graph, writer, audit, opts);
		var report = await svc.RunAsync();

		report.GroupsFailed.Should().Be(1);
		report.GroupsSucceeded.Should().Be(1);
		report.GrantsAdded.Should().Be(1, "only the working group's member was granted");
	}

	[Fact]
	public async Task Admin_Mapping_Uses_Null_Department_Id()
	{
		var graph = new StubGraph(membership: new()
		{
			[GroupLibrarians] = new[] { UserAlice },
		});
		var writer = new RecordingWriter { InsertResult = true };
		var audit = new RecordingAudit();

		var opts = new EntraGroupSyncOptions
		{
			Enabled = true,
			GroupMappings =
			{
				new EntraGroupMapping
				{
					GroupObjectId = GroupLibrarians,
					Role = Role.Admin,
					DepartmentId = string.Empty, // intentional
				},
			},
		};

		var svc = MakeService(graph, writer, audit, opts);
		await svc.RunAsync();

		writer.GrantCalls.Should().ContainSingle();
		writer.GrantCalls[0].DepartmentId.Should().BeNull();
		writer.GrantCalls[0].Role.Should().Be(Role.Admin);
	}

	private static EntraGroupSyncService MakeService(
		IGraphMembershipClient graph,
		IUserAuthorizationWriter writer,
		IAuditWriter audit,
		EntraGroupSyncOptions options)
		=> new(
			graph,
			writer,
			audit,
			Options.Create(options),
			NullLogger<EntraGroupSyncService>.Instance);

	private sealed class StubGraph : IGraphMembershipClient
	{
		private readonly Dictionary<string, Guid[]> _membership;

		public StubGraph(Dictionary<string, Guid[]>? membership = null)
		{
			_membership = membership ?? new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase);
		}

		public string? ThrowingGroup { get; set; }

		public Task<IReadOnlyList<Guid>> ListGroupMemberOidsAsync(string groupObjectId, CancellationToken cancellationToken = default)
		{
			if (string.Equals(ThrowingGroup, groupObjectId, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("simulated Graph 500");
			}

			return Task.FromResult<IReadOnlyList<Guid>>(_membership.GetValueOrDefault(groupObjectId, Array.Empty<Guid>()));
		}
	}

	private sealed class RecordingWriter : IUserAuthorizationWriter
	{
		public List<GrantCall> GrantCalls { get; } = new();
		public List<ReconcileCall> ReconcileCalls { get; } = new();
		public bool InsertResult { get; init; }
		public int ReconcileResult { get; init; }

		public Task<bool> GrantAsync(Guid userId, Guid? departmentId, Role role, string sourceGroupId, CancellationToken cancellationToken = default)
		{
			GrantCalls.Add(new GrantCall(userId, departmentId, role, sourceGroupId));
			return Task.FromResult(InsertResult);
		}

		public Task<int> ReconcileAsync(string sourceGroupId, IReadOnlyCollection<Guid> keepUserIds, CancellationToken cancellationToken = default)
		{
			ReconcileCalls.Add(new ReconcileCall(sourceGroupId, keepUserIds.ToArray()));
			return Task.FromResult(ReconcileResult);
		}

		public Task<IReadOnlyList<UserAuthorization>> ListBySourceGroupAsync(string sourceGroupId, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<UserAuthorization>>(Array.Empty<UserAuthorization>());

		public sealed record GrantCall(Guid UserId, Guid? DepartmentId, Role Role, string SourceGroupId);
		public sealed record ReconcileCall(string SourceGroupId, IReadOnlyCollection<Guid> KeepUserIds);
	}

	private sealed class RecordingAudit : IAuditWriter
	{
		public List<AuditEvent> Events { get; } = new();

		public Task WriteAsync(AuditEvent evt, AuditCriticality criticality = AuditCriticality.Critical, CancellationToken cancellationToken = default)
		{
			Events.Add(evt);
			return Task.CompletedTask;
		}
	}
}
