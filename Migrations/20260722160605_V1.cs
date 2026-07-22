using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class V1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ApiKey = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ContextWindow = table.Column<int>(type: "int", nullable: false),
                    InputPricePerMillionTokens = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    OutputPricePerMillionTokens = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SupportsVision = table.Column<bool>(type: "bit", nullable: false),
                    CreatedByUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiModels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OrgUnitName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UserMemory = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NotifyInApp = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    NotifyByEmail = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    NotifyOnGate = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    NotifyOnCompleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    NotifyOnFailed = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Associates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    PersonalNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    GlobalId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    OrgUnitCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    OrganizationUnit = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Mobiphone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PickupAddress = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Gender = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Position = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    StandardWorkingHour = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Costcenter = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    LeadingPerson = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    EmployeeSubGroup = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    HiredDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsIndirect = table.Column<bool>(type: "bit", nullable: false),
                    LeavingDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Birthday = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Associates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ActorUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ActorRole = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AfterJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EvalRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PromptKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TargetModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetModelName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    JudgeModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JudgeModelName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ScenarioCount = table.Column<int>(type: "int", nullable: false),
                    CompletedCount = table.Column<int>(type: "int", nullable: false),
                    AverageScore = table.Column<double>(type: "float", nullable: true),
                    TotalTokens = table.Column<long>(type: "bigint", nullable: false),
                    TotalCost = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedByUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvalRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EvalScenarios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PromptKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    UserInput = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Criteria = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedByUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvalScenarios", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Feedbacks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedByUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SubmittedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Feedbacks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProjectName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Link = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsRead = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrgUnits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CostCenter = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DiscManagerLId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DisciplinaryResponsible = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OrgUnitCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TargetResponsible = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TrgtManagerLId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TypeOrganize = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    IsDepartment = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgUnits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    BackendGitUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FrontendGitUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsUseBoschTemplate = table.Column<bool>(type: "bit", nullable: false),
                    CreatedByUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OrgUnitCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ConversationSummary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SummarizedTurnCount = table.Column<int>(type: "int", nullable: false),
                    UserMemoryHarvestedTurnCount = table.Column<int>(type: "int", nullable: false),
                    ChecklistGapHarvested = table.Column<bool>(type: "bit", nullable: false),
                    DomainKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequirementCoverageMap = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CoverageHarvestedTurnCount = table.Column<int>(type: "int", nullable: false),
                    DecisionLog = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DecisionHarvestedTurnCount = table.Column<int>(type: "int", nullable: false),
                    OpenQuestions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PlannedScope = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WorkedExamples = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    InterviewOutlookHarvestedTurnCount = table.Column<int>(type: "int", nullable: false),
                    PocFeedbackHarvestedCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PromptTemplateVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PromptKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ChangeNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedByUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptTemplateVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Permission = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ToolDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(3000)", maxLength: 3000, nullable: false),
                    ServiceType = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    MethodName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Color = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Temperature = table.Column<double>(type: "float", nullable: false),
                    AiModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LearnedChecklistNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedByUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Agents_AiModels_AiModelId",
                        column: x => x.AiModelId,
                        principalTable: "AiModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvalResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvalRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvalScenarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScenarioName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Output = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PromptVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PromptVersionNumber = table.Column<int>(type: "int", nullable: true),
                    Score = table.Column<int>(type: "int", nullable: true),
                    JudgeReasoning = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsSuccess = table.Column<bool>(type: "bit", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TargetTokens = table.Column<int>(type: "int", nullable: false),
                    JudgeTokens = table.Column<int>(type: "int", nullable: false),
                    TargetCost = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    JudgeCost = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvalResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvalResults_EvalRuns_EvalRunId",
                        column: x => x.EvalRunId,
                        principalTable: "EvalRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeedbackAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FeedbackId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StoredPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeedbackAttachments_Feedbacks_FeedbackId",
                        column: x => x.FeedbackId,
                        principalTable: "Feedbacks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PocComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PageView = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ElementLabel = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ElementPath = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    XPercent = table.Column<double>(type: "float", nullable: false),
                    YPercent = table.Column<double>(type: "float", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedByUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PocComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PocComments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectSourceFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StoredPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ExtractedText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PageCount = table.Column<int>(type: "int", nullable: false),
                    IsVisionSource = table.Column<bool>(type: "bit", nullable: false),
                    UploadedByUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectSourceFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectSourceFiles_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    CurrentStage = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowRuns_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Suggestions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SuggestionsMultiSelect = table.Column<bool>(type: "bit", nullable: false),
                    FlowDiagram = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TokenUsed = table.Column<int>(type: "int", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentConversations_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentConversations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentDomainChecklistNotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DomainKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDomainChecklistNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentDomainChecklistNotes_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentModelCallLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AgentName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequestJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResponseText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PromptTokens = table.Column<int>(type: "int", nullable: false),
                    CompletionTokens = table.Column<int>(type: "int", nullable: false),
                    TotalTokens = table.Column<int>(type: "int", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    HttpStatusCode = table.Column<int>(type: "int", nullable: true),
                    IsSuccess = table.Column<bool>(type: "bit", nullable: false),
                    Step = table.Column<int>(type: "int", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentModelCallLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentModelCallLogs_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentModelCallLogs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentTools",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToolDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTools", x => new { x.AgentId, x.ToolDefinitionId });
                    table.ForeignKey(
                        name: "FK_AgentTools_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentTools_ToolDefinitions_ToolDefinitionId",
                        column: x => x.ToolDefinitionId,
                        principalTable: "ToolDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Folder = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    VersionName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsApproved = table.Column<bool>(type: "bit", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FilePath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TokenUsed = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectDocuments_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProjectDocuments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Input = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RevisionFeedback = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Output = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentTasks_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AgentTasks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentTasks_WorkflowRuns_WorkflowRunId",
                        column: x => x.WorkflowRunId,
                        principalTable: "WorkflowRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectDocumentRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionNumber = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ChangeNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    VersionName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectDocumentRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectDocumentRevisions_ProjectDocuments_ProjectDocumentId",
                        column: x => x.ProjectDocumentId,
                        principalTable: "ProjectDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentConversations_AgentId",
                table: "AgentConversations",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentConversations_ProjectId",
                table: "AgentConversations",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentDomainChecklistNotes_AgentId_DomainKey",
                table: "AgentDomainChecklistNotes",
                columns: new[] { "AgentId", "DomainKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentModelCallLogs_AgentId",
                table: "AgentModelCallLogs",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentModelCallLogs_CreatedAt",
                table: "AgentModelCallLogs",
                column: "CreatedAt")
                .Annotation("SqlServer:Include", new[] { "ProjectId", "ModelId", "PromptTokens", "CompletionTokens" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentModelCallLogs_ProjectId_AgentId_CreatedAt",
                table: "AgentModelCallLogs",
                columns: new[] { "ProjectId", "AgentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentModelCallLogs_WorkflowRunId",
                table: "AgentModelCallLogs",
                column: "WorkflowRunId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_AiModelId",
                table: "Agents",
                column: "AiModelId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_RoleKey",
                table: "Agents",
                column: "RoleKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentTasks_AgentId",
                table: "AgentTasks",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTasks_ProjectId_Status_CreatedAt",
                table: "AgentTasks",
                columns: new[] { "ProjectId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTasks_Status_CreatedAt",
                table: "AgentTasks",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTasks_WorkflowRunId",
                table: "AgentTasks",
                column: "WorkflowRunId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTools_ToolDefinitionId",
                table: "AgentTools",
                column: "ToolDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_AiModels_ModelId",
                table: "AiModels",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_Username",
                table: "AppUsers",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Associates_GlobalId",
                table: "Associates",
                column: "GlobalId");

            migrationBuilder.CreateIndex(
                name: "IX_Associates_OrgUnitCode",
                table: "Associates",
                column: "OrgUnitCode");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Category_CreatedAt",
                table: "AuditLogs",
                columns: new[] { "Category", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CreatedAt",
                table: "AuditLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EvalResults_EvalRunId",
                table: "EvalResults",
                column: "EvalRunId");

            migrationBuilder.CreateIndex(
                name: "IX_EvalResults_EvalScenarioId",
                table: "EvalResults",
                column: "EvalScenarioId");

            migrationBuilder.CreateIndex(
                name: "IX_EvalRuns_CreatedAt",
                table: "EvalRuns",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EvalRuns_Status_CreatedAt",
                table: "EvalRuns",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EvalScenarios_IsActive_CreatedAt",
                table: "EvalScenarios",
                columns: new[] { "IsActive", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackAttachments_FeedbackId",
                table: "FeedbackAttachments",
                column: "FeedbackId");

            migrationBuilder.CreateIndex(
                name: "IX_Feedbacks_CreatedAt",
                table: "Feedbacks",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Feedbacks_CreatedByUsername_CreatedAt",
                table: "Feedbacks",
                columns: new[] { "CreatedByUsername", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecipientUsername_IsRead_CreatedAt",
                table: "Notifications",
                columns: new[] { "RecipientUsername", "IsRead", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnits_OrgUnitCode",
                table: "OrgUnits",
                column: "OrgUnitCode");

            migrationBuilder.CreateIndex(
                name: "IX_PocComments_ProjectId_Status_CreatedAt",
                table: "PocComments",
                columns: new[] { "ProjectId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocumentRevisions_ProjectDocumentId_RevisionNumber",
                table: "ProjectDocumentRevisions",
                columns: new[] { "ProjectDocumentId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocuments_AgentId",
                table: "ProjectDocuments",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocuments_ProjectId",
                table: "ProjectDocuments",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_CreatedByUsername_CreatedAt",
                table: "Projects",
                columns: new[] { "CreatedByUsername", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSourceFiles_ProjectId_CreatedAt",
                table: "ProjectSourceFiles",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PromptTemplateVersions_PromptKey_IsActive",
                table: "PromptTemplateVersions",
                columns: new[] { "PromptKey", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_PromptTemplateVersions_PromptKey_VersionNumber",
                table: "PromptTemplateVersions",
                columns: new[] { "PromptKey", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_Role_Permission",
                table: "RolePermissions",
                columns: new[] { "Role", "Permission" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ToolDefinitions_Name",
                table: "ToolDefinitions",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ToolDefinitions_ServiceType_MethodName",
                table: "ToolDefinitions",
                columns: new[] { "ServiceType", "MethodName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_ProjectId_Status_CreatedAt",
                table: "WorkflowRuns",
                columns: new[] { "ProjectId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentConversations");

            migrationBuilder.DropTable(
                name: "AgentDomainChecklistNotes");

            migrationBuilder.DropTable(
                name: "AgentModelCallLogs");

            migrationBuilder.DropTable(
                name: "AgentTasks");

            migrationBuilder.DropTable(
                name: "AgentTools");

            migrationBuilder.DropTable(
                name: "AppUsers");

            migrationBuilder.DropTable(
                name: "Associates");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "EvalResults");

            migrationBuilder.DropTable(
                name: "EvalScenarios");

            migrationBuilder.DropTable(
                name: "FeedbackAttachments");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "OrgUnits");

            migrationBuilder.DropTable(
                name: "PocComments");

            migrationBuilder.DropTable(
                name: "ProjectDocumentRevisions");

            migrationBuilder.DropTable(
                name: "ProjectSourceFiles");

            migrationBuilder.DropTable(
                name: "PromptTemplateVersions");

            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "WorkflowRuns");

            migrationBuilder.DropTable(
                name: "ToolDefinitions");

            migrationBuilder.DropTable(
                name: "EvalRuns");

            migrationBuilder.DropTable(
                name: "Feedbacks");

            migrationBuilder.DropTable(
                name: "ProjectDocuments");

            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "AiModels");
        }
    }
}
