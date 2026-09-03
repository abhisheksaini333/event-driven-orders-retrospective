#!/usr/bin/env python3
"""Gate vulnerability reports without mistaking malformed scan output for a clean result."""
import argparse
import json
from pathlib import Path

def records(value, field):
    if not isinstance(value, list) or any(not isinstance(item, dict) for item in value):
        raise ValueError('Invalid report collection: ' + field)
    return value

def severity(issue, field, allowed):
    value = issue.get(field)
    if not isinstance(value, str) or value.lower() not in allowed:
        raise ValueError('Unknown vulnerability severity.')
    return value.lower()

def nuget_findings(report):
    if not isinstance(report, dict) or type(report.get('version')) is not int or report['version'] != 1:
        raise ValueError('Expected a version-1 NuGet report.')
    projects = records(report.get('projects'), 'projects')
    if not projects: raise ValueError('NuGet report has no projects.')
    findings = []
    for project in projects:
        if not isinstance(project.get('path'), str) or not project['path']: raise ValueError('Missing project path.')
        for framework in records(project.get('frameworks', []), 'frameworks'):
            for group in ('topLevelPackages', 'transitivePackages'):
                for package in records(framework.get(group, []), group):
                    for issue in records(package.get('vulnerabilities', []), 'vulnerabilities'):
                        level = severity(issue, 'severity', ('low','moderate','high','critical'))
                        if not isinstance(package.get('id'), str) or not package['id']: raise ValueError('Missing package identity.')
                        if level in ('high','critical'):
                            findings.append({'package':package['id'], 'severity':level, 'advisory':issue.get('advisoryurl','')})
    return findings

def trivy_findings(report):
    if not isinstance(report, dict) or type(report.get('SchemaVersion')) is not int or report['SchemaVersion'] != 2:
        raise ValueError('Expected a version-2 Trivy report.')
    findings = []
    for result in records(report.get('Results'), 'Results'):
        vulnerabilities = result.get('Vulnerabilities')
        for issue in records([] if vulnerabilities is None else vulnerabilities, 'Vulnerabilities'):
            level = severity(issue, 'Severity', ('unknown','low','medium','high','critical'))
            if not isinstance(issue.get('PkgName'), str) or not isinstance(issue.get('VulnerabilityID'), str): raise ValueError('Missing vulnerability identity.')
            if level in ('high','critical'):
                findings.append({'package':issue['PkgName'], 'severity':level, 'advisory':issue['VulnerabilityID']})
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
