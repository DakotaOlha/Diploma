using Diploma.Core.Models;
using Diploma.Core.Services;

namespace Diploma.Core.Interfaces;

public interface ISettingsService
{
    AppSettings Current { get; }
    Task LoadAsync();
    Task SaveAsync();
    void ApplyToProfileService(ModeProfileService profileService);
}
