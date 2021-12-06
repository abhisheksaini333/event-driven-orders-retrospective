#!/usr/bin/env python3
"""Gate vulnerability reports without mistaking malformed scan output for a clean result."""
import argparse
import json
from pathlib import Path

def nuget_findings(report):
    if report.get('version') != 1 or not isinstance(report.get('projects'), list) or not report['projects']:
        raise ValueError('Expected a version-1 NuGet package report with projects.')
    findings = []
    for project in report['projects']:
        if not isinstance(project.get('path'), str): raise ValueError('Missing project path.')
        for framework in project.get('frameworks', []):
            for group in ('topLevelPackages', 'transitivePackages'):
                for package in framework.get(group, []):
                    for issue in package.get('vulnerabilities', []):
                        severity = issue.get('severity', '').lower()
                        if severity not in ('low', 'moderate', 'high', 'critical'): raise ValueError('Unknown NuGet severity.')
                        if severity in ('high', 'critical'):
                            findings.append({'package': package['id'], 'severity': severity, 'advisory': issue.get('advisoryurl', '')})
    return findings

def trivy_findings(report):
    if report.get('SchemaVersion') != 2 or not isinstance(report.get('Results'), list):
        raise ValueError('Expected a version-2 Trivy report with results.')
    findings = []
    for result in report['Results']:
        for issue in result.get('Vulnerabilities') or []:
            severity = issue.get('Severity', '').lower()
            if severity not in ('unknown', 'low', 'medium', 'high', 'critical'): raise ValueError('Unknown Trivy severity.')
            if severity in ('high', 'critical'):
                findings.append({'package': issue['PkgName'], 'severity': severity, 'advisory': issue['VulnerabilityID']})
    return findings

def main(argv=None):
    parser = argparse.ArgumentParser(); group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument('--nuget', type=Path); group.add_argument('--trivy', type=Path); args = parser.parse_args(argv)
    try:
        findings = nuget_findings(json.loads(args.nuget.read_text())) if args.nuget else trivy_findings(json.loads(args.trivy.read_text()))
    except (ValueError, KeyError, TypeError, OSError) as error:
        print(json.dumps({'status': 'invalid_report', 'error_type': type(error).__name__})); return 2
    print(json.dumps({'status': 'blocked' if findings else 'passed', 'findings': findings}, indent=2))
    return 1 if findings else 0

if __name__ == '__main__':
    raise SystemExit(main())
