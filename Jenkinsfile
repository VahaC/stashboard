pipeline {
    agent none

    options {
        disableConcurrentBuilds()
    }

    triggers {
        pollSCM('H/2 * * * *')
    }

    stages {
        stage('Checkout') {
            agent any
            steps {
                git branch: 'main',
                    credentialsId: 'github-pat',
                    url: 'https://github.com/VahaC/stashboard.git'
                stash includes: '**', name: 'source'
            }
        }

        stage('.NET Build & Test') {
            agent {
                docker { image 'mcr.microsoft.com/dotnet/sdk:10.0' }
            }
            steps {
                unstash 'source'
                sh 'dotnet restore Stashboard.slnx'
                sh 'dotnet build Stashboard.slnx -c Release --no-restore'
                sh 'dotnet test tests/Stashboard.Tests --no-build -c Release --logger "trx;LogFileName=test-results.trx"'
            }
            post {
                always {
                    junit allowEmptyResults: true, testResults: '**/test-results.trx'
                }
            }
        }

        stage('Frontend Test') {
            agent {
                docker { image 'node:22-alpine' }
            }
            steps {
                unstash 'source'
                dir('frontend') {
                    sh 'npm ci'
                    sh 'npm run test'
                }
            }
        }
    }

    post {
        failure {
            node('built-in') {
                withCredentials([
                    string(credentialsId: 'telegram-bot-token', variable: 'TG_TOKEN'),
                    string(credentialsId: 'telegram-chat-id', variable: 'TG_CHAT')
                ]) {
                    sh '''
                        curl -s -X POST https://api.telegram.org/bot${TG_TOKEN}/sendMessage \
                          -d chat_id=${TG_CHAT} \
                          -d text="❌ Stashboard build FAILED: ${JOB_NAME} #${BUILD_NUMBER}
${BUILD_URL}"
                    '''
                }
            }
        }
    }
}
