using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftRide.TripService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialTripDb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActiveDriverAssignments",
                columns: table => new
                {
                    DriverId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TripId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActiveDriverAssignments", x => x.DriverId);
                });

            migrationBuilder.CreateTable(
                name: "ActiveRiderTrips",
                columns: table => new
                {
                    RiderId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TripId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActiveRiderTrips", x => x.RiderId);
                });

            migrationBuilder.CreateTable(
                name: "CommandReceipts",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RequestHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ResponseJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommandReceipts", x => new { x.Scope, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "TripPaymentJobs",
                columns: table => new
                {
                    TripId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    MaxAttempts = table.Column<int>(type: "int", nullable: false),
                    DeadlineUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextAttemptUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Owner = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RequiresAction = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripPaymentJobs", x => x.TripId);
                });

            migrationBuilder.CreateTable(
                name: "Trips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RiderId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FareAmount = table.Column<decimal>(type: "decimal(18,0)", nullable: true),
                    FareCurrency = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    IsFareLocked = table.Column<bool>(type: "bit", nullable: false),
                    CandidateDriverId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OfferedDriverId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AssignedDriverId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RejectionCount = table.Column<int>(type: "int", nullable: false),
                    MaxDriverRejections = table.Column<int>(type: "int", nullable: false),
                    PaymentIdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastPaymentFailure = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PickupName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PickupAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    PickupLat = table.Column<double>(type: "float", nullable: false),
                    PickupLon = table.Column<double>(type: "float", nullable: false),
                    DestName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DestAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    DestLat = table.Column<double>(type: "float", nullable: false),
                    DestLon = table.Column<double>(type: "float", nullable: false),
                    PaymentMethod = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    MockTokenAlias = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DistanceKm = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    EstimatedMinutes = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trips", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TripDriverRejections",
                columns: table => new
                {
                    TripId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DriverId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripDriverRejections", x => new { x.TripId, x.DriverId });
                    table.ForeignKey(
                        name: "FK_TripDriverRejections_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActiveDriverAssignments_TripId",
                table: "ActiveDriverAssignments",
                column: "TripId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActiveRiderTrips_TripId",
                table: "ActiveRiderTrips",
                column: "TripId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActiveDriverAssignments");

            migrationBuilder.DropTable(
                name: "ActiveRiderTrips");

            migrationBuilder.DropTable(
                name: "CommandReceipts");

            migrationBuilder.DropTable(
                name: "TripDriverRejections");

            migrationBuilder.DropTable(
                name: "TripPaymentJobs");

            migrationBuilder.DropTable(
                name: "Trips");
        }
    }
}
