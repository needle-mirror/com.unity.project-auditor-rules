# Changelog
All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [1.0.3] - 2026-03-13

# Fixed
* Recognize Array.Clear as a valid reset method, and do not log delayCall issues in the Domain Reload Roslyn Analyzer.

## [1.0.2] - 2026-02-25

# Added
* Added a database of all Obsolete Unity API. Newer versions of Project Auditor will be able to use this to help with upgrades.

# Fixed
* Fixed various issues with the Domain Reload Roslyn Analyzer. It now detects more variable reset scenarios, and disallows multiple ResetInitializeOnLoad attributes.

## [1.0.1] - 2025-10-31

# Changed
* Removed MemoryIgnoreVoidReturn area, in favor of using a new returnType entry for filtering based on return type.

## [1.0.0] - 2025-09-26

### Added
* Migrated rules and Roslyn Analyzers from com-unity-project-auditor package, as we migrate the tool to be bundled with the Unity Editor as a module.

